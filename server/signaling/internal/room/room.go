// Package room owns one Host/Viewer pairing per support ID. Lock order: manager then room.
// Network I/O never occurs under either lock.
package room

import (
	"context"
	"crypto/rand"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"io"
	"math/big"
	"strings"
	"sync"
	"time"
)

const (
	RoleHost   = "host"
	RoleViewer = "viewer"
)

var (
	ErrNotFound  = errors.New("세션 코드를 찾을 수 없습니다")
	ErrExpired   = errors.New("세션이 만료되었습니다")
	ErrBadToken  = errors.New("host 토큰이 올바르지 않습니다")
	ErrBadRole   = errors.New("role은 host 또는 viewer여야 합니다")
	ErrSlotTaken = errors.New("해당 역할이 이미 사용 중입니다")
	ErrConsumed  = errors.New("이미 사용한 세션 코드입니다")
	ErrCapacity  = errors.New("서버 세션 상한에 도달했습니다")
)

type Conn interface {
	WriteJSON(context.Context, any) error
}
type Peer struct {
	ID, Role, DisplayName string
	Conn                  Conn
	// Kick 은 이 연결을 끊는다. 새 Viewer가 접속을 이어받을 때 쓴다.
	Kick  func()
	ready bool
}
type Room struct {
	Code, HostToken, SessionID string
	CreatedAt, ExpiresAt       time.Time
	mu                         sync.Mutex
	host, viewer               *Peer
	consumed, hostUsed, ended  bool
	negotiation                int
}

// Negotiation tracks the Host's offer generation. Only the Host advances it.
func (r *Room) Negotiation(role, kind string, generation int) (int, bool) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.ended {
		return 0, false
	}
	if r.negotiation == 0 {
		r.negotiation = 1
	}
	if generation == 0 {
		generation = 1
	}
	if kind == "offer" && role == RoleHost && generation == r.negotiation+1 && generation <= 256 {
		r.negotiation = generation
		return generation, true
	}
	if generation < r.negotiation {
		return r.negotiation, false
	}
	return r.negotiation, generation == r.negotiation
}
func (r *Room) otherLocked(p *Peer) *Peer {
	var other *Peer
	if r.host == p {
		other = r.viewer
	} else if r.viewer == p {
		other = r.host
	}
	if other != nil && other.ready {
		return other
	}
	return nil
}
func (r *Room) Other(p *Peer) *Peer { r.mu.Lock(); defer r.mu.Unlock(); return r.otherLocked(p) }
func (r *Room) Empty() bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.host == nil && r.viewer == nil
}

type Manager struct {
	ttl      time.Duration
	now      func() time.Time
	random   io.Reader
	mu       sync.Mutex
	rooms    map[string]*Room
	maxRooms int
}

func NewManager(ttl time.Duration) *Manager { return NewManagerWithEntropy(ttl, rand.Reader) }
func NewManagerWithEntropy(ttl time.Duration, entropy io.Reader) *Manager {
	return &Manager{ttl: ttl, now: time.Now, random: entropy, rooms: make(map[string]*Room), maxRooms: 1000}
}
func (m *Manager) SetClock(now func() time.Time) { m.mu.Lock(); defer m.mu.Unlock(); m.now = now }
func (m *Manager) SetLimit(limit int)            { m.mu.Lock(); defer m.mu.Unlock(); m.maxRooms = limit }
func (m *Manager) TTL() time.Duration            { return m.ttl }

// Create makes a room. A non-empty fixedCode is the Host's stable device ID: it is
// reused across restarts, and a room whose pairing already ended is replaced.
func (m *Manager) Create(fixedCode string) (*Room, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if len(m.rooms) >= m.maxRooms {
		return nil, ErrCapacity
	}
	token, err := randomToken(m.random)
	if err != nil {
		return nil, err
	}
	sid, err := randomToken(m.random)
	if err != nil {
		return nil, err
	}
	for i := 0; i < 10; i++ {
		code := fixedCode
		if code == "" {
			var err error
			if code, err = randomDigits(m.random, 9); err != nil {
				return nil, err
			}
		}
		if old, exists := m.rooms[code]; exists {
			old.mu.Lock()
			replace := fixedCode != "" && (old.ended || old.host == nil)
			old.mu.Unlock()
			if !replace {
				if fixedCode != "" {
					return nil, ErrSlotTaken
				}
				continue
			}
		}
		now := m.now()
		r := &Room{Code: code, HostToken: token, SessionID: sid, CreatedAt: now, ExpiresAt: now.Add(m.ttl)}
		m.rooms[code] = r
		return r, nil
	}
	return nil, errors.New("세션 코드 생성에 실패했습니다")
}
func (m *Manager) Get(code string) (*Room, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	r, ok := m.rooms[code]
	if !ok {
		return nil, ErrNotFound
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if !m.now().Before(r.ExpiresAt) {
		if r.host == nil && r.viewer == nil {
			delete(m.rooms, code)
		}
		return nil, ErrExpired
	}
	if r.ended {
		return nil, ErrConsumed
	}
	return r, nil
}

// Reserve performs lookup, expiry, authentication and reservation atomically.
// Reserved peers cannot receive relay messages before their ACK succeeds.
func (m *Manager) Reserve(code, role, token string, p *Peer) (*Room, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	r, ok := m.rooms[code]
	if !ok {
		return nil, ErrNotFound
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	if !m.now().Before(r.ExpiresAt) {
		if r.host == nil && r.viewer == nil {
			delete(m.rooms, code)
		}
		return nil, ErrExpired
	}
	if r.ended {
		return nil, ErrConsumed
	}
	switch role {
	case RoleHost:
		if subtle.ConstantTimeCompare([]byte(token), []byte(r.HostToken)) != 1 {
			return nil, ErrBadToken
		}
		if r.host != nil {
			return nil, ErrSlotTaken
		}
		if r.hostUsed {
			return nil, ErrConsumed
		}
		r.host = p
		r.hostUsed = true
		// A connected Host keeps its stable device room alive until it leaves.
		r.ExpiresAt = m.now().Add(100 * 365 * 24 * time.Hour)
	case RoleViewer:
		if r.viewer != nil {
			return nil, ErrSlotTaken
		}
		if r.consumed {
			return nil, ErrConsumed
		}
		r.viewer = p
		r.consumed = true
	default:
		return nil, ErrBadRole
	}
	return r, nil
}
func (m *Manager) Activate(r *Room, p *Peer) (*Peer, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	r.mu.Lock()
	defer r.mu.Unlock()
	if m.rooms[r.Code] != r || r.ended || (r.host != p && r.viewer != p) {
		return nil, ErrConsumed
	}
	p.ready = true
	return r.otherLocked(p), nil
}

// Any departure ends the pairing. The Host then opens a fresh room under the same support ID.
func (m *Manager) Leave(r *Room, p *Peer) *Peer {
	m.mu.Lock()
	defer m.mu.Unlock()
	r.mu.Lock()
	defer r.mu.Unlock()
	other := r.otherLocked(p)
	if r.host == p {
		r.host = nil
		r.ended = true
	}
	if r.viewer == p {
		r.viewer = nil
		r.ended = true
	}
	if m.rooms[r.Code] == r && r.host == nil && r.viewer == nil {
		delete(m.rooms, r.Code)
	}
	return other
}

// Viewer returns the viewer currently holding the room's viewer slot.
func (m *Manager) Viewer(code string) *Peer {
	m.mu.Lock()
	defer m.mu.Unlock()
	r, ok := m.rooms[code]
	if !ok {
		return nil
	}
	r.mu.Lock()
	defer r.mu.Unlock()
	return r.viewer
}
func (m *Manager) Count() int { m.mu.Lock(); defer m.mu.Unlock(); return len(m.rooms) }
func (m *Manager) Sweep() int {
	m.mu.Lock()
	defer m.mu.Unlock()
	n := 0
	now := m.now()
	for code, r := range m.rooms {
		r.mu.Lock()
		if !now.Before(r.ExpiresAt) && r.host == nil && r.viewer == nil {
			delete(m.rooms, code)
			n++
		}
		r.mu.Unlock()
	}
	return n
}
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
func randomDigits(reader io.Reader, n int) (string, error) {
	var sb strings.Builder
	for i := 0; i < n; i++ {
		d, err := rand.Int(reader, big.NewInt(10))
		if err != nil {
			return "", err
		}
		sb.WriteByte(byte('0' + d.Int64()))
	}
	return sb.String(), nil
}
func randomToken(reader io.Reader) (string, error) {
	b := make([]byte, 24)
	if _, err := io.ReadFull(reader, b); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(b), nil
}
