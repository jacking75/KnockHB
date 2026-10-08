// Package room owns atomic one-use pairing. Lock order: manager then room.
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
	ErrBadPIN    = errors.New("PIN이 올바르지 않습니다")
	ErrBadToken  = errors.New("host 토큰이 올바르지 않습니다")
	ErrBadRole   = errors.New("role은 host 또는 viewer여야 합니다")
	ErrSlotTaken = errors.New("해당 역할이 이미 사용 중입니다")
	ErrConsumed  = errors.New("이미 사용한 세션 코드입니다")
	ErrLocked    = errors.New("PIN 시도 횟수를 초과했습니다")
	ErrCapacity  = errors.New("서버 세션 상한에 도달했습니다")
)

type Conn interface {
	WriteJSON(context.Context, any) error
}
type Peer struct {
	ID, Role, DisplayName string
	Conn                  Conn
	ready                 bool
}
type Room struct {
	Code, PIN, HostToken, SessionID string
	CreatedAt, ExpiresAt            time.Time
	mu                              sync.Mutex
	host, viewer                    *Peer
	consumed, hostUsed, ended       bool
	pinFailures                     int
	negotiation                     int
	lastRefresh                     time.Time
}

// Negotiation never changes the one-use pairing identity or consumes another PIN.
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
func (r *Room) Refresh(role string, now time.Time) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	if r.ended || role != RoleHost || r.host == nil || r.viewer == nil || !r.host.ready || !r.viewer.ready || now.Sub(r.lastRefresh) < 5*time.Second {
		return false
	}
	r.lastRefresh = now
	return true
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
func (m *Manager) Create() (*Room, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if len(m.rooms) >= m.maxRooms {
		return nil, ErrCapacity
	}
	pin, err := randomDigits(m.random, 4)
	if err != nil {
		return nil, err
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
		code, err := randomDigits(m.random, 9)
		if err != nil {
			return nil, err
		}
		if _, exists := m.rooms[code]; exists {
			continue
		}
		now := m.now()
		r := &Room{Code: code, PIN: pin, HostToken: token, SessionID: sid, CreatedAt: now, ExpiresAt: now.Add(m.ttl)}
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
func (m *Manager) Reserve(code, role, pin, token string, p *Peer) (*Room, error) {
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
	case RoleViewer:
		if r.pinFailures >= 5 {
			return nil, ErrLocked
		}
		if subtle.ConstantTimeCompare([]byte(pin), []byte(r.PIN)) != 1 {
			r.pinFailures++
			return nil, ErrBadPIN
		}
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

// Any departure invalidates approval. A replacement peer never inherits it.
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
