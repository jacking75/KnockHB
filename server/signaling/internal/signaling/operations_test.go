package signaling

import (
	"bytes"
	"context"
	"encoding/json"
	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestAuditDurableDuplicateConflictAndRecovery(t *testing.T) {
	directory := filepath.Join(t.TempDir(), "audit")
	store, err := OpenAuditStore(directory)
	if err != nil {
		t.Fatal(err)
	}
	e := AuditEvent{ID: strings.Repeat("a", 32), UTC: time.Now().UTC(), Kind: 7, Result: 3, Session: "sid", Generation: 1}
	if store.Append(e) != nil || store.Append(e) != nil {
		t.Fatal("append/duplicate")
	}
	data, _ := os.ReadFile(filepath.Join(directory, "audit.ndjson"))
	if bytes.Count(data, []byte{10}) != 1 {
		t.Fatal("duplicate persisted")
	}
	changed := e
	changed.Result = 4
	if store.Append(changed) == nil {
		t.Fatal("conflicting replay ACKed")
	}
	recovered, err := OpenAuditStore(directory)
	if err != nil || recovered.Append(e) != nil || recovered.Append(changed) == nil {
		t.Fatal("recovery dedup drift")
	}
	if err := os.Remove(filepath.Join(directory, "audit.ndjson")); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(directory, "audit.ndjson"), 0700); err != nil {
		t.Fatal(err)
	}
	e.ID = strings.Repeat("b", 32)
	if store.Append(e) == nil {
		t.Fatal("disk path failure hidden")
	}
	if store.Healthy() {
		t.Fatal("disk path failure left readiness healthy")
	}
}
func TestAuditAuthSchemaAndReadiness(t *testing.T) {
	directory := t.TempDir()
	key := filepath.Join(directory, "key")
	os.WriteFile(key, []byte(strings.Repeat("x", 32)), 0600)
	s := New(Config{OperatorSecretFile: key, AuditDirectory: filepath.Join(directory, "audit")}, nil)
	handler := s.Routes()
	event := AuditEvent{ID: strings.Repeat("c", 32), UTC: time.Now().UTC(), Kind: 1, Result: 0, Session: "sid"}
	data, _ := json.Marshal(event)
	for _, test := range []struct {
		body   []byte
		auth   string
		status int
	}{{data, "", 401}, {[]byte(`{"EventId":"secret","stdout":"private"}`), "Bearer " + strings.Repeat("x", 32), 400}, {data, "Bearer " + strings.Repeat("x", 32), 204}} {
		r := httptest.NewRequest("POST", "/audit", bytes.NewReader(test.body))
		r.Header.Set("Authorization", test.auth)
		w := httptest.NewRecorder()
		handler.ServeHTTP(w, r)
		if w.Code != test.status {
			t.Fatal("audit auth/schema status", w.Code)
		}
	}
	auditPath := filepath.Join(directory, "audit", "audit.ndjson")
	if err := os.Remove(auditPath); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(auditPath, 0700); err != nil {
		t.Fatal(err)
	}
	event.ID = strings.Repeat("d", 32)
	data, _ = json.Marshal(event)
	r := httptest.NewRequest("POST", "/audit", bytes.NewReader(data))
	r.Header.Set("Authorization", "Bearer "+strings.Repeat("x", 32))
	failed := httptest.NewRecorder()
	handler.ServeHTTP(failed, r)
	if failed.Code != 503 {
		t.Fatal("storage failure ACKed", failed.Code)
	}
	w := httptest.NewRecorder()
	handler.ServeHTTP(w, httptest.NewRequest("GET", "/readyz", nil))
	if w.Code != 503 {
		t.Fatal("storage failure ready")
	}
}
func TestBoundedHundredRoomsShutdown(t *testing.T) { runPool(t, time.Second) }
func TestLongLoad(t *testing.T) {
	seconds, err := strconv.Atoi(os.Getenv("RA_LONG_LOAD_SECONDS"))
	if err != nil {
		t.Skip("explicit off-screen 600s or longer load bundle only")
	}
	if seconds < 600 || seconds > 7200 {
		t.Fatal("load duration must be 600..7200 seconds")
	}
	runPool(t, time.Duration(seconds)*time.Second)
}
func runPool(t *testing.T, duration time.Duration) {
	s, ts := newTestServer(t)
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	var readers sync.WaitGroup
	var peers []*websocket.Conn
	defer func() {
		cancel()
		for _, peer := range peers {
			peer.CloseNow()
		}
		readers.Wait()
	}()
	for i := 0; i < 100; i++ {
		session := createSession(t, ts)
		h := dial(t, ts)
		joinHost(t, h, session)
		v := dial(t, ts)
		joinViewer(t, v, session)
		read(t, h)
		peers = append(peers, h, v)
		for _, peer := range []*websocket.Conn{h, v} {
			readers.Add(1)
			go func(c *websocket.Conn) {
				defer readers.Done()
				for {
					var msg Msg
					if wsjson.Read(ctx, c, &msg) != nil {
						return
					}
					if msg.Type == "ping" {
						if wsjson.Write(ctx, c, Msg{Version: 1, Type: "pong"}) != nil {
							return
						}
					}
				}
			}(peer)
		}
	}
	if s.rooms.Count() != 100 {
		t.Fatal("room count")
	}
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	timer := time.NewTimer(duration)
	defer timer.Stop()
	for {
		select {
		case <-ticker.C:
			var mem runtime.MemStats
			runtime.ReadMemStats(&mem)
			t.Logf("sample rooms=%d goroutines=%d heap=%d", s.rooms.Count(), runtime.NumGoroutine(), mem.HeapAlloc)
		case <-timer.C:
			goto shutdown
		}
	}
shutdown:
	clock := time.Now()
	s.Shutdown()
	if time.Since(clock) > 6*time.Second {
		t.Fatal("shutdown deadline")
	}
	deadline := time.Now().Add(time.Second)
	for s.rooms.Count() != 0 && time.Now().Before(deadline) {
		runtime.Gosched()
	}
	if s.rooms.Count() != 0 {
		t.Fatal("residual rooms")
	}
}
