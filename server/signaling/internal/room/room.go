// Package room 은 세션 코드로 Host와 Viewer를 같은 룸에 묶는다.
//
// 이 패키지는 WebSocket을 알지 못한다. 연결은 Conn 인터페이스로만 다루므로
// 테스트에서 가짜 연결을 끼워 넣을 수 있고, 전송 계층을 바꿔도 여기는 그대로다.
//
// 누수 주의: 룸은 두 피어가 모두 나가면 즉시 지워야 하고, 아무도 참가하지
// 않은 룸도 TTL이 지나면 청소돼야 한다. 지우는 경로가 없으면 맵이 무한히 자란다.
package room

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"math/big"
	"strings"
	"sync"
	"time"
)

// 역할 슬롯. 각각 하나씩만 존재한다.
const (
	RoleHost   = "host"
	RoleViewer = "viewer"
)

var (
	ErrNotFound  = errors.New("세션 코드를 찾을 수 없습니다")
	ErrExpired   = errors.New("세션이 만료되었습니다")
	ErrBadPIN    = errors.New("PIN이 올바르지 않습니다")
	ErrBadToken  = errors.New("host 토큰이 올바르지 않습니다")
	ErrBadRole   = errors.New("role은 host 또는 viewer여야 합니다")
	ErrSlotTaken = errors.New("해당 역할이 이미 사용 중입니다")
)

// Conn 은 룸이 피어에게 무언가를 보낼 때 필요한 최소한의 동작이다.
type Conn interface {
	WriteJSON(ctx context.Context, v any) error
}

// Peer 는 룸에 붙은 한쪽 연결이다.
type Peer struct {
	ID   string
	Role string
	Conn Conn
}

// Room 은 하나의 지원 세션이다.
type Room struct {
	Code      string
	PIN       string
	HostToken string
	CreatedAt time.Time
	ExpiresAt time.Time

	mu     sync.Mutex
	host   *Peer
	viewer *Peer
}

// Authorize 는 참가 자격을 확인한다.
//
// host 슬롯은 POST /session 응답으로만 알 수 있는 토큰을 요구한다.
// 코드만 아는 제3자가 host 자리를 먼저 차지해 Viewer의 offer를 가로채는 것을 막는다.
//
// TODO(T3.1): 실패 횟수 제한·잠금. 지금은 검증만 한다.
func (r *Room) Authorize(role, pin, token string) error {
	switch role {
	case RoleHost:
		if subtle.ConstantTimeCompare([]byte(token), []byte(r.HostToken)) != 1 {
			return ErrBadToken
		}
	case RoleViewer:
		if subtle.ConstantTimeCompare([]byte(pin), []byte(r.PIN)) != 1 {
			return ErrBadPIN
		}
	default:
		return ErrBadRole
	}
	return nil
}

// Join 은 피어를 역할 슬롯에 넣는다. 이미 차 있으면 ErrSlotTaken.
func (r *Room) Join(p *Peer) error {
	r.mu.Lock()
	defer r.mu.Unlock()

	switch p.Role {
	case RoleHost:
		if r.host != nil {
			return ErrSlotTaken
		}
		r.host = p
	case RoleViewer:
		if r.viewer != nil {
			return ErrSlotTaken
		}
		r.viewer = p
	default:
		return ErrBadRole
	}
	return nil
}

// Leave 는 피어를 슬롯에서 뺀다. 이미 다른 피어로 교체된 경우는 건드리지 않는다.
func (r *Room) Leave(p *Peer) {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.host == p {
		r.host = nil
	}
	if r.viewer == p {
		r.viewer = nil
	}
}

// Other 는 반대편 피어를 돌려준다. 없으면 nil.
func (r *Room) Other(p *Peer) *Peer {
	r.mu.Lock()
	defer r.mu.Unlock()

	if r.host == p {
		return r.viewer
	}
	if r.viewer == p {
		return r.host
	}
	return nil
}

// Empty 는 아무도 붙어 있지 않으면 true.
func (r *Room) Empty() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.host == nil && r.viewer == nil
}

// Manager 는 코드 → 룸 매핑을 들고 있다.
type Manager struct {
	ttl time.Duration
	now func() time.Time

	mu    sync.Mutex
	rooms map[string]*Room
}

// NewManager 는 룸 관리자를 만든다. ttl은 세션 코드의 유효 기간이다.
func NewManager(ttl time.Duration) *Manager {
	return &Manager{
		ttl:   ttl,
		now:   time.Now,
		rooms: make(map[string]*Room),
	}
}

// SetClock 은 테스트에서 시간을 조작하기 위한 것이다.
func (m *Manager) SetClock(now func() time.Time) { m.now = now }

// TTL 은 세션 유효 기간이다.
func (m *Manager) TTL() time.Duration { return m.ttl }

// Create 는 새 룸과 코드·PIN·host 토큰을 발급한다.
func (m *Manager) Create() (*Room, error) {
	pin, err := randomDigits(4)
	if err != nil {
		return nil, err
	}
	token, err := randomToken()
	if err != nil {
		return nil, err
	}

	now := m.now()

	m.mu.Lock()
	defer m.mu.Unlock()

	// 코드 충돌은 사실상 없지만, 있으면 다시 뽑는다.
	var code string
	for i := 0; i < 10; i++ {
		c, err := randomDigits(9)
		if err != nil {
			return nil, err
		}
		if _, dup := m.rooms[c]; !dup {
			code = c
			break
		}
	}
	if code == "" {
		return nil, errors.New("세션 코드 생성에 실패했습니다")
	}

	r := &Room{
		Code:      code,
		PIN:       pin,
		HostToken: token,
		CreatedAt: now,
		ExpiresAt: now.Add(m.ttl),
	}
	m.rooms[code] = r
	return r, nil
}

// Get 은 코드로 룸을 찾는다. 만료된 룸은 지우고 ErrExpired를 돌려준다.
func (m *Manager) Get(code string) (*Room, error) {
	m.mu.Lock()
	defer m.mu.Unlock()

	r, ok := m.rooms[code]
	if !ok {
		return nil, ErrNotFound
	}
	if m.now().After(r.ExpiresAt) {
		delete(m.rooms, code)
		return nil, ErrExpired
	}
	return r, nil
}

// Remove 는 룸을 지운다. 두 피어가 모두 나갔을 때 호출한다.
func (m *Manager) Remove(code string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.rooms, code)
}

// Count 는 살아 있는 룸 수다. 누수 확인·모니터링용.
func (m *Manager) Count() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	return len(m.rooms)
}

// Sweep 은 만료됐고 아무도 붙어 있지 않은 룸을 청소한다.
//
// 접속 중인 룸은 만료돼도 끊지 않는다. 진행 중인 지원 세션을 시간이 지났다는
// 이유로 끊어 버리면 안 되기 때문이다. 코드의 유효 기간은 "참가 가능한 기간"이지
// "세션의 최대 길이"가 아니다.
func (m *Manager) Sweep() int {
	m.mu.Lock()
	defer m.mu.Unlock()

	now := m.now()
	removed := 0
	for code, r := range m.rooms {
		if now.After(r.ExpiresAt) && r.Empty() {
			delete(m.rooms, code)
			removed++
		}
	}
	return removed
}

// StartSweeper 는 ctx가 살아 있는 동안 주기적으로 Sweep을 돈다.
func (m *Manager) StartSweeper(ctx context.Context, every time.Duration) {
	go func() {
		t := time.NewTicker(every)
		defer t.Stop()
		for {
			select {
			case <-ctx.Done():
				return
			case <-t.C:
				m.Sweep()
			}
		}
	}()
}

func randomDigits(n int) (string, error) {
	var sb strings.Builder
	sb.Grow(n)
	for i := 0; i < n; i++ {
		d, err := rand.Int(rand.Reader, big.NewInt(10))
		if err != nil {
			return "", err
		}
		sb.WriteByte(byte('0' + d.Int64()))
	}
	return sb.String(), nil
}

func randomToken() (string, error) {
	b := make([]byte, 24)
	if _, err := rand.Read(b); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}
