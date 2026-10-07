package signaling

import (
	"context"
	"github.com/coder/websocket"
	"github.com/remoteassist/signaling/internal/room"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

func TestACKBarrier(t *testing.T) {
	s, ts := newTestServer(t)
	sess := createSession(t, ts)
	reserved := make(chan struct{})
	release := make(chan struct{})
	s.beforeACK = func(p *room.Peer) {
		if p.Role == room.RoleHost {
			close(reserved)
			<-release
		}
	}
	host := dial(t, ts)
	write(t, host, Msg{Type: "join", Room: sess.Code, Role: "host", Token: sess.HostToken})
	<-reserved
	viewer := dial(t, ts)
	if ack := joinViewer(t, viewer, sess); ack.Type != "joined" || *ack.PeerPresent {
		t.Fatalf("unexpected ACK: %+v", ack)
	}
	close(release)
	if ack := read(t, host); ack.Type != "joined" {
		t.Fatalf("ACK not first: %+v", ack)
	}
	if msg := read(t, viewer); msg.Type != "peer-joined" {
		t.Fatal(msg)
	}
}
func TestMalformedFramesRejected(t *testing.T) {
	for _, data := range []string{`{"v":0,"type":"join"}`, `{"v":1,"v":1,"type":"join"}`, `{"v":1,"type":"join","ice":{"candidate":"a","candidate":"b"}}`, strings.Repeat("a", readLimit+1)} {
		t.Run(data[:min(len(data), 30)], func(t *testing.T) {
			_, ts := newTestServer(t)
			c := dial(t, ts)
			ctx, cancel := context.WithTimeout(context.Background(), time.Second)
			defer cancel()
			_ = c.Write(ctx, websocket.MessageText, []byte(data))
			if _, _, err := c.Read(ctx); err == nil {
				t.Fatal("malformed frame accepted")
			}
		})
	}
}
func TestViewerCannotGrantConsent(t *testing.T) {
	_, ts := newTestServer(t)
	sess := createSession(t, ts)
	h := dial(t, ts)
	joinHost(t, h, sess)
	v := dial(t, ts)
	joinViewer(t, v, sess)
	read(t, h)
	write(t, v, Msg{Type: "consent", Permissions: []string{"screen", "admin"}})
	if msg := read(t, h); msg.Type != "peer-left" {
		t.Fatal("viewer grant relayed", msg)
	}
}
func TestShutdownClosesHijackedSockets(t *testing.T) {
	s, ts := newTestServer(t)
	sess := createSession(t, ts)
	c := dial(t, ts)
	joinHost(t, c, sess)
	s.Shutdown()
	ctx, cancel := context.WithTimeout(context.Background(), time.Second)
	defer cancel()
	if _, _, err := c.Read(ctx); err == nil {
		t.Fatal("socket survived shutdown")
	}
}
func TestUnresponsivePeerExpiresAndRoomIsRemoved(t *testing.T) {
	s := New(Config{SessionTTL: time.Minute}, nil)
	s.readLease = 100 * time.Millisecond
	s.heartbeatInterval = 20 * time.Millisecond
	handlerDone := make(chan struct{})
	routes := s.Routes()
	ts := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		routes.ServeHTTP(w, r)
		if r.URL.Path == "/ws" {
			close(handlerDone)
		}
	}))
	defer ts.Close()
	sess := createSession(t, ts)
	c := dial(t, ts)
	joinHost(t, c, sess)
	// Consume server pings without replying: TCP remains open, application lease must expire.
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()
	for {
		if _, _, err := c.Read(ctx); err != nil {
			break
		}
	}
	select {
	case <-handlerDone:
	case <-ctx.Done():
		t.Fatal("blackhole handler survived deadline")
	}
	if s.Rooms().Count() != 0 {
		t.Fatal("expired connection leaked room")
	}
}
func TestBinaryAndInvalidUTF8AreRejected(t *testing.T) {
	for _, fixture := range []struct {
		kind websocket.MessageType
		data []byte
	}{
		{websocket.MessageBinary, []byte(`{"v":1,"type":"join"}`)},
		{websocket.MessageText, []byte{'{', '"', 'v', '"', ':', '1', ',', '"', 't', 'y', 'p', 'e', '"', ':', '"', 0xff, '"', '}'}},
	} {
		_, ts := newTestServer(t)
		c := dial(t, ts)
		ctx, cancel := context.WithTimeout(context.Background(), time.Second)
		_ = c.Write(ctx, fixture.kind, fixture.data)
		if _, _, err := c.Read(ctx); err == nil {
			t.Fatal("invalid transport accepted")
		}
		cancel()
	}
}
func TestDisplayNameUsesUTF16UnitsAndRejectsControls(t *testing.T) {
	for _, valid := range []string{"지원자", strings.Repeat("가", 80), strings.Repeat("😀", 40)} {
		if !validDisplayName(valid) {
			t.Fatal("valid Unicode name rejected")
		}
	}
	for _, invalid := range []string{"", " ", " a", "a\n", strings.Repeat("가", 81), strings.Repeat("😀", 41)} {
		if validDisplayName(invalid) {
			t.Fatal("invalid name accepted")
		}
	}
}
