package signaling

import (
	"context"
	"encoding/json"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
)

// 테스트 공용 헬퍼 ---------------------------------------------------------

func newTestServer(t *testing.T) (*Server, *httptest.Server) {
	t.Helper()
	s := New(Config{SessionTTL: time.Minute}, slog.New(slog.NewTextHandler(io.Discard, nil)))
	ts := httptest.NewServer(s.Routes())
	t.Cleanup(ts.Close)
	return s, ts
}

func createSession(t *testing.T, ts *httptest.Server) createSessionResponse {
	t.Helper()
	resp, err := http.Post(ts.URL+"/session", "application/json", nil)
	if err != nil {
		t.Fatalf("POST /session: %v", err)
	}
	defer resp.Body.Close()
	if resp.StatusCode != http.StatusOK {
		t.Fatalf("POST /session 상태코드 = %d, want 200", resp.StatusCode)
	}
	var out createSessionResponse
	if err := json.NewDecoder(resp.Body).Decode(&out); err != nil {
		t.Fatalf("응답 디코드: %v", err)
	}
	return out
}

func dial(t *testing.T, ts *httptest.Server) *websocket.Conn {
	t.Helper()
	url := "ws" + strings.TrimPrefix(ts.URL, "http") + "/ws"
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	c, _, err := websocket.Dial(ctx, url, nil)
	if err != nil {
		t.Fatalf("dial: %v", err)
	}
	t.Cleanup(func() { _ = c.CloseNow() })
	return c
}

func write(t *testing.T, c *websocket.Conn, m Msg) {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := wsjson.Write(ctx, c, m); err != nil {
		t.Fatalf("write %s: %v", m.Type, err)
	}
}

func read(t *testing.T, c *websocket.Conn) Msg {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	var m Msg
	if err := wsjson.Read(ctx, c, &m); err != nil {
		t.Fatalf("read: %v", err)
	}
	return m
}

// joinHost / joinViewer 는 참가 후 joined ack까지 확인한다.
func joinHost(t *testing.T, c *websocket.Conn, s createSessionResponse) Msg {
	t.Helper()
	write(t, c, Msg{Type: "join", Room: s.Code, Role: "host", Token: s.HostToken})
	return read(t, c)
}

func joinViewer(t *testing.T, c *websocket.Conn, s createSessionResponse) Msg {
	t.Helper()
	write(t, c, Msg{Type: "join", Room: s.Code, Role: "viewer", PIN: s.PIN})
	return read(t, c)
}

// 테스트 -------------------------------------------------------------------

// 세션 발급이 계약대로 나오는지.
func TestCreateSession(t *testing.T) {
	_, ts := newTestServer(t)
	s := createSession(t, ts)

	if len(s.Code) != 9 {
		t.Errorf("code 길이 = %d, want 9 (%q)", len(s.Code), s.Code)
	}
	if len(s.PIN) != 4 {
		t.Errorf("pin 길이 = %d, want 4 (%q)", len(s.PIN), s.PIN)
	}
	if s.HostToken == "" {
		t.Error("hostToken이 비어 있다")
	}
	if s.ExpiresIn != 60 {
		t.Errorf("expiresIn = %d, want 60", s.ExpiresIn)
	}
}

// DoD: 한쪽이 보낸 메시지가 다른 쪽에 도착한다.
func TestRelayOfferAnswerICE(t *testing.T) {
	_, ts := newTestServer(t)
	sess := createSession(t, ts)

	host := dial(t, ts)
	if ack := joinHost(t, host, sess); ack.Type != "joined" || *ack.PeerPresent {
		t.Fatalf("host ack = %+v, want joined/peerPresent=false", ack)
	}

	viewer := dial(t, ts)
	if ack := joinViewer(t, viewer, sess); ack.Type != "joined" || !*ack.PeerPresent {
		t.Fatalf("viewer ack = %+v, want joined/peerPresent=true", ack)
	}

	// Host는 peer-joined를 받고 offer를 만든다.
	if m := read(t, host); m.Type != "peer-joined" || m.Role != "viewer" {
		t.Fatalf("host가 받은 것 = %+v, want peer-joined/viewer", m)
	}

	// offer: host -> viewer
	write(t, host, Msg{Type: "offer", SDP: "v=0 fake-offer"})
	if m := read(t, viewer); m.Type != "offer" || m.SDP != "v=0 fake-offer" {
		t.Fatalf("viewer가 받은 offer = %+v", m)
	}

	// answer: viewer -> host
	write(t, viewer, Msg{Type: "answer", SDP: "v=0 fake-answer"})
	if m := read(t, host); m.Type != "answer" || m.SDP != "v=0 fake-answer" {
		t.Fatalf("host가 받은 answer = %+v", m)
	}

	// ice: viewer -> host (필드가 보존되는지)
	mid := "0"
	idx := 0
	write(t, viewer, Msg{Type: "ice", ICE: &ICECandidate{
		Candidate: "candidate:1 1 udp 2130706431 192.0.2.1 50000 typ host",
		SDPMid:    &mid, SDPMLineIndex: &idx,
	}})
	got := read(t, host)
	if got.Type != "ice" || got.ICE == nil {
		t.Fatalf("host가 받은 ice = %+v", got)
	}
	if got.ICE.Candidate != "candidate:1 1 udp 2130706431 192.0.2.1 50000 typ host" {
		t.Errorf("candidate가 바뀌었다: %q", got.ICE.Candidate)
	}
	if got.ICE.SDPMid == nil || *got.ICE.SDPMid != "0" {
		t.Errorf("sdpMid가 보존되지 않았다: %+v", got.ICE.SDPMid)
	}
}

// 릴레이할 때 인증 필드가 상대에게 새면 안 된다.
func TestRelayStripsCredentials(t *testing.T) {
	_, ts := newTestServer(t)
	sess := createSession(t, ts)

	host := dial(t, ts)
	joinHost(t, host, sess)
	viewer := dial(t, ts)
	joinViewer(t, viewer, sess)
	read(t, host) // peer-joined

	// viewer가 악의적으로(혹은 실수로) 인증 필드를 실어 보낸다.
	write(t, viewer, Msg{Type: "answer", SDP: "sdp", PIN: sess.PIN, Token: sess.HostToken})

	got := read(t, host)
	if got.PIN != "" {
		t.Errorf("pin이 릴레이됐다: %q", got.PIN)
	}
	if got.Token != "" {
		t.Errorf("token이 릴레이됐다: %q", got.Token)
	}
}

// DoD: 연결을 끊으면 룸이 정리된다.
func TestRoomCleanedUpOnDisconnect(t *testing.T) {
	s, ts := newTestServer(t)
	sess := createSession(t, ts)

	if got := s.Rooms().Count(); got != 1 {
		t.Fatalf("발급 후 룸 수 = %d, want 1", got)
	}

	host := dial(t, ts)
	joinHost(t, host, sess)
	viewer := dial(t, ts)
	joinViewer(t, viewer, sess)
	read(t, host) // peer-joined

	// viewer가 끊기면 host는 peer-left를 받고, 룸은 아직 남아 있다.
	_ = viewer.Close(websocket.StatusNormalClosure, "bye")
	if m := read(t, host); m.Type != "peer-left" || m.Role != "viewer" {
		t.Fatalf("host가 받은 것 = %+v, want peer-left/viewer", m)
	}
	if got := s.Rooms().Count(); got != 1 {
		t.Fatalf("viewer 이탈 후 룸 수 = %d, want 1 (host가 남아 있다)", got)
	}

	// host까지 끊기면 룸이 사라진다.
	_ = host.Close(websocket.StatusNormalClosure, "bye")

	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if s.Rooms().Count() == 0 {
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("모두 이탈 후에도 룸이 남아 있다: %d개", s.Rooms().Count())
}

// 참가 인증 실패 경로.
func TestJoinRejections(t *testing.T) {
	_, ts := newTestServer(t)
	sess := createSession(t, ts)

	cases := []struct {
		name     string
		msg      Msg
		wantCode string
	}{
		{"없는 코드", Msg{Type: "join", Room: "000000000", Role: "viewer", PIN: sess.PIN}, "not-found"},
		{"틀린 PIN", Msg{Type: "join", Room: sess.Code, Role: "viewer", PIN: "0000"}, "bad-pin"},
		{"틀린 토큰", Msg{Type: "join", Room: sess.Code, Role: "host", Token: "nope"}, "bad-token"},
		{"이상한 role", Msg{Type: "join", Room: sess.Code, Role: "admin", PIN: sess.PIN}, "bad-role"},
		{"첫 메시지가 join이 아님", Msg{Type: "offer", SDP: "x"}, "bad-request"},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			c := dial(t, ts)
			write(t, c, tc.msg)
			m := read(t, c)
			if m.Type != "error" || m.Code != tc.wantCode {
				t.Fatalf("응답 = %+v, want error/%s", m, tc.wantCode)
			}
		})
	}
}

// 같은 역할로 두 번 들어올 수 없다.
func TestSlotTaken(t *testing.T) {
	_, ts := newTestServer(t)
	sess := createSession(t, ts)

	first := dial(t, ts)
	joinHost(t, first, sess)

	second := dial(t, ts)
	write(t, second, Msg{Type: "join", Room: sess.Code, Role: "host", Token: sess.HostToken})
	if m := read(t, second); m.Type != "error" || m.Code != "slot-taken" {
		t.Fatalf("응답 = %+v, want error/slot-taken", m)
	}
}

// 만료된 세션은 참가할 수 없고, 청소 대상이 된다.
func TestExpiredSession(t *testing.T) {
	s, ts := newTestServer(t)
	sess := createSession(t, ts)

	// 시계를 TTL 너머로 민다.
	s.Rooms().SetClock(func() time.Time { return time.Now().Add(2 * time.Minute) })

	c := dial(t, ts)
	write(t, c, Msg{Type: "join", Room: sess.Code, Role: "viewer", PIN: sess.PIN})
	if m := read(t, c); m.Type != "error" || m.Code != "expired" {
		t.Fatalf("응답 = %+v, want error/expired", m)
	}
	if got := s.Rooms().Count(); got != 0 {
		t.Errorf("만료 조회 후 룸 수 = %d, want 0", got)
	}
}

// 아무도 참가하지 않은 룸도 만료되면 청소된다.
func TestSweepRemovesAbandonedRooms(t *testing.T) {
	s, ts := newTestServer(t)
	createSession(t, ts)
	createSession(t, ts)

	if got := s.Rooms().Count(); got != 2 {
		t.Fatalf("룸 수 = %d, want 2", got)
	}

	s.Rooms().SetClock(func() time.Time { return time.Now().Add(2 * time.Minute) })
	if removed := s.Rooms().Sweep(); removed != 2 {
		t.Errorf("청소된 룸 = %d, want 2", removed)
	}
	if got := s.Rooms().Count(); got != 0 {
		t.Errorf("청소 후 룸 수 = %d, want 0", got)
	}
}

// 진행 중인 세션은 만료 시간이 지나도 청소하지 않는다.
// 코드의 유효 기간은 "참가 가능 기간"이지 "세션 최대 길이"가 아니다.
func TestSweepKeepsActiveRooms(t *testing.T) {
	s, ts := newTestServer(t)
	sess := createSession(t, ts)

	host := dial(t, ts)
	joinHost(t, host, sess)

	s.Rooms().SetClock(func() time.Time { return time.Now().Add(2 * time.Minute) })
	if removed := s.Rooms().Sweep(); removed != 0 {
		t.Errorf("청소된 룸 = %d, want 0 (접속 중인 세션은 유지)", removed)
	}
	if got := s.Rooms().Count(); got != 1 {
		t.Errorf("룸 수 = %d, want 1", got)
	}
}

// 연결이 붙었다 끊기기를 반복해도 고루틴이 쌓이지 않아야 한다.
//
// race 검출기는 cgo(gcc)가 필요해 개발 머신에서 못 돌리는 경우가 있다.
// 이 테스트는 cgo 없이도 "가장 흔한 실수"인 고루틴 누수만은 잡아 둔다.
func TestNoGoroutineLeak(t *testing.T) {
	s, ts := newTestServer(t)

	cycle := func() {
		sess := createSession(t, ts)
		host := dial(t, ts)
		joinHost(t, host, sess)
		viewer := dial(t, ts)
		joinViewer(t, viewer, sess)
		read(t, host) // peer-joined
		write(t, host, Msg{Type: "offer", SDP: "x"})
		read(t, viewer)
		_ = viewer.Close(websocket.StatusNormalClosure, "")
		_ = host.Close(websocket.StatusNormalClosure, "")
	}

	cycle() // 워밍업: 전송 계층 고루틴을 미리 띄운다
	settle(t, s, 0)
	base := runtime.NumGoroutine()

	for i := 0; i < 20; i++ {
		cycle()
	}
	settle(t, s, 0)

	got := runtime.NumGoroutine()
	if got > base+5 {
		t.Fatalf("고루틴이 샜다: 기준 %d → 현재 %d", base, got)
	}
	if n := s.Rooms().Count(); n != 0 {
		t.Fatalf("룸이 남았다: %d개", n)
	}
}

// settle 은 룸 수가 want로 떨어지고 고루틴이 잦아들 때까지 기다린다.
func settle(t *testing.T, s *Server, want int) {
	t.Helper()
	deadline := time.Now().Add(3 * time.Second)
	for time.Now().Before(deadline) {
		if s.Rooms().Count() == want {
			time.Sleep(100 * time.Millisecond) // 전송 계층 정리 여유
			return
		}
		time.Sleep(10 * time.Millisecond)
	}
	t.Fatalf("룸 수가 %d로 정리되지 않았다: 현재 %d", want, s.Rooms().Count())
}
