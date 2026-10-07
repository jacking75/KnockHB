package room

import (
	"bytes"
	"errors"
	"sync"
	"testing"
	"time"
)

func reserve(t *testing.T, m *Manager, r *Room, role string) *Peer {
	t.Helper()
	p := &Peer{Role: role}
	if _, err := m.Reserve(r.Code, role, r.PIN, r.HostToken, p); err != nil {
		t.Fatal(err)
	}
	return p
}
func TestReservedPeerIsInvisibleUntilACK(t *testing.T) {
	m := NewManager(time.Minute)
	r, _ := m.Create()
	h := reserve(t, m, r, RoleHost)
	v := reserve(t, m, r, RoleViewer)
	if r.Other(h) != nil || r.Other(v) != nil {
		t.Fatal("unacknowledged peer published")
	}
	if other, err := m.Activate(r, v); err != nil || other != nil {
		t.Fatalf("viewer activation: %v %v", other, err)
	}
	if other, err := m.Activate(r, h); err != nil || other != v {
		t.Fatal("second activation must discover ready peer")
	}
}
func TestLeaveInvalidatesPendingACKAndReplacement(t *testing.T) {
	m := NewManager(time.Minute)
	r, _ := m.Create()
	h := reserve(t, m, r, RoleHost)
	v := reserve(t, m, r, RoleViewer)
	m.Leave(r, v)
	if _, err := m.Activate(r, h); !errors.Is(err, ErrConsumed) {
		t.Fatal("late ACK succeeded after departure")
	}
	if _, err := m.Reserve(r.Code, RoleViewer, r.PIN, "", &Peer{Role: RoleViewer}); !errors.Is(err, ErrConsumed) {
		t.Fatal("replacement inherited approval")
	}
	m.Leave(r, h)
	if m.Count() != 0 {
		t.Fatal("room leak")
	}
}
func TestZeroEntropyWrongPINAndLockout(t *testing.T) {
	m := NewManagerWithEntropy(time.Minute, bytes.NewReader(make([]byte, 4096)))
	r, err := m.Create()
	if err != nil {
		t.Fatal(err)
	}
	if r.PIN != "0000" {
		t.Fatal("fixture is not deterministic")
	}
	for i := 0; i < 5; i++ {
		if _, err := m.Reserve(r.Code, RoleViewer, "0001", "", &Peer{Role: RoleViewer}); !errors.Is(err, ErrBadPIN) {
			t.Fatal(err)
		}
	}
	if _, err := m.Reserve(r.Code, RoleViewer, r.PIN, "", &Peer{Role: RoleViewer}); !errors.Is(err, ErrLocked) {
		t.Fatal("valid PIN bypassed lockout")
	}
}
func TestActiveExpiredRoomRemainsMapped(t *testing.T) {
	m := NewManager(time.Minute)
	now := time.Unix(1000, 0)
	m.SetClock(func() time.Time { return now })
	r, _ := m.Create()
	h := reserve(t, m, r, RoleHost)
	m.SetClock(func() time.Time { return now.Add(time.Minute) })
	if _, err := m.Get(r.Code); !errors.Is(err, ErrExpired) {
		t.Fatal("expiry boundary")
	}
	if m.Count() != 1 || m.Sweep() != 0 {
		t.Fatal("active expired room deleted")
	}
	m.Leave(r, h)
	if m.Count() != 0 {
		t.Fatal("leak")
	}
}
func TestJoinLeaveSweepRaceCannotJoinUnmappedRoom(t *testing.T) {
	for i := 0; i < 100; i++ {
		m := NewManager(time.Minute)
		r, _ := m.Create()
		h := reserve(t, m, r, RoleHost)
		start := make(chan struct{})
		var wg sync.WaitGroup
		wg.Add(2)
		var joined *Room
		var err error
		v := &Peer{Role: RoleViewer}
		go func() { defer wg.Done(); <-start; joined, err = m.Reserve(r.Code, RoleViewer, r.PIN, "", v) }()
		go func() { defer wg.Done(); <-start; m.Leave(r, h); m.Sweep() }()
		close(start)
		wg.Wait()
		if err == nil {
			if joined != r || m.Count() != 1 {
				t.Fatal("join succeeded on unmapped room")
			}
			if _, err := m.Activate(r, v); !errors.Is(err, ErrConsumed) {
				t.Fatal("ended room activated")
			}
			m.Leave(r, v)
		}
		if m.Count() != 0 {
			t.Fatal("room leak")
		}
	}
}
