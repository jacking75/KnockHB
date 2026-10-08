package room

import (
	"testing"
	"time"
)

func TestNegotiationGenerationsRejectStaleAndViewerAdvance(t *testing.T) {
	r := &Room{}
	if _, ok := r.Negotiation(RoleHost, "offer", 1); !ok {
		t.Fatal("initial")
	}
	if _, ok := r.Negotiation(RoleViewer, "answer", 2); ok {
		t.Fatal("viewer advanced")
	}
	if gen, ok := r.Negotiation(RoleHost, "offer", 2); !ok || gen != 2 {
		t.Fatal("host advance")
	}
	if gen, ok := r.Negotiation(RoleViewer, "ice", 1); ok || gen != 2 {
		t.Fatal("stale generation")
	}
	r.ended = true
	if _, ok := r.Negotiation(RoleHost, "offer", 3); ok {
		t.Fatal("ended resumed")
	}
}
func TestTURNRefreshRequiresBothReadyAndFiveSecondHold(t *testing.T) {
	r := &Room{host: &Peer{ready: true}, viewer: &Peer{ready: true}}
	now := time.Now()
	if r.Refresh(RoleViewer, now) || !r.Refresh(RoleHost, now) || r.Refresh(RoleHost, now.Add(4*time.Second)) || !r.Refresh(RoleHost, now.Add(5*time.Second)) {
		t.Fatal("refresh role/deadline")
	}
	r.viewer.ready = false
	if r.Refresh(RoleHost, now.Add(10*time.Second)) {
		t.Fatal("unready refresh")
	}
}
