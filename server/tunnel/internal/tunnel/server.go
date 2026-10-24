// Package tunnel 은 터널링 서버 HTTP/WebSocket 엔드포인트를 제공한다.
//
// 서버가 하는 일은 두 가지뿐이다.
//  1. 세션 코드 발급 (POST /session). Host가 고정 device code를 지정할 수 있다
//  2. 같은 룸의 두 피어 사이에서 SDP/ICE를 그대로 옮겨 주기 (GET /ws)
//
// 미디어는 여기를 거치지 않는다. 협상이 끝나면 Host와 Viewer가 지정 IP로 직접 연결하고,
// 서버는 더 이상 관여하지 않는다. STUN/TURN 서버는 쓰지 않는다.
//
// 와이어 포맷의 기준은 docs/README.md#protocol 다. 이 파일이 아니다.
package tunnel

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"strings"
	"sync"
	"sync/atomic"
	"time"
	"unicode"
	"unicode/utf16"

	"github.com/coder/websocket"
	"github.com/coder/websocket/wsjson"
	"github.com/remoteassist/tunnel/internal/room"
)

const (
	// SDP는 후보가 많이 붙으면 수십 KB까지 간다. 넉넉히 잡되 무제한은 아니다.
	readLimit = 128 << 10

	// 연결 후 join이 오기까지 기다리는 시간. 붙여 놓고 아무것도 안 보내는
	// 연결이 쌓이지 않게 한다.
	joinTimeout = 10 * time.Second

	// 한 번의 쓰기에 허용하는 시간. 느린 피어가 반대편을 붙잡아 두지 않게 한다.
	writeTimeout = 5 * time.Second

	// 끊긴 뒤 상대에게 peer-left를 알릴 때 쓰는 시간.
	notifyTimeout = 3 * time.Second
)

// Msg 는 터널링 서버 메시지다. 필드명 기준은 docs/README.md#protocol.
type Msg struct {
	Version      int    `json:"v"`
	SessionID    string `json:"sessionId,omitempty"`
	ConnectionID string `json:"connectionId,omitempty"`
	Generation   int    `json:"generation,omitempty"`
	DisplayName  string `json:"displayName,omitempty"`
	Type         string `json:"type"`
	Room         string `json:"room,omitempty"`
	Role         string `json:"role,omitempty"`

	// 참가 인증용. 절대 반대편으로 릴레이하지 않는다.
	Token string `json:"token,omitempty"`

	SDP string        `json:"sdp,omitempty"`
	ICE *ICECandidate `json:"ice,omitempty"`

	PeerPresent *bool `json:"peerPresent,omitempty"`

	// error 전용
	Code   string `json:"code,omitempty"`
	Reason string `json:"reason,omitempty"`
}

// ICECandidate 는 브라우저 RTCIceCandidate의 필드명을 그대로 쓴다.
type ICECandidate struct {
	Candidate     string  `json:"candidate"`
	SDPMid        *string `json:"sdpMid,omitempty"`
	SDPMLineIndex *int    `json:"sdpMLineIndex,omitempty"`
}

// Config 는 서버 설정이다.
type Config struct {
	// SessionTTL 은 세션 코드로 참가할 수 있는 기간이다.
	SessionTTL time.Duration
	// OriginPatterns 는 브라우저 뷰어의 허용 Origin이다. 비우면 동일 호스트만 허용된다.
	OriginPatterns []string
	// 선택 사항. 비우면 /audit·/metrics를 쓰지 않는다.
	OperatorSecretFile string
	AuditDirectory     string
}

// Server 는 터널링 서버다.
type Server struct {
	rooms             *room.Manager
	cfg               Config
	log               *slog.Logger
	mu                sync.Mutex
	connections       map[*websocket.Conn]struct{}
	stopping          bool
	pending           int
	beforeACK         func(*room.Peer) // deterministic barrier, nil outside tests
	readLease         time.Duration
	heartbeatInterval time.Duration
	audit             *AuditStore
	auditRate         *requestLimiter
	startupError      error
	failures          atomic.Int64
}

// New 는 서버를 만든다.
func New(cfg Config, log *slog.Logger) *Server {
	audit, auditErr := OpenAuditStore(cfg.AuditDirectory)
	if cfg.SessionTTL <= 0 {
		cfg.SessionTTL = 10 * time.Minute
	}
	if log == nil {
		log = slog.Default()
	}
	return &Server{
		audit: audit, auditRate: newLimiter(120, time.Minute, 4096), startupError: auditErr,
		rooms:             room.NewManager(cfg.SessionTTL),
		cfg:               cfg,
		log:               log,
		connections:       make(map[*websocket.Conn]struct{}),
		readLease:         15 * time.Second,
		heartbeatInterval: 5 * time.Second,
	}
}

// Rooms 는 룸 관리자다. 테스트·모니터링에서 쓴다.
func (s *Server) Rooms() *room.Manager { return s.rooms }

// StartSweeper 는 만료 룸 청소를 시작한다.
func (s *Server) StartSweeper(ctx context.Context) {
	s.rooms.StartSweeper(ctx, time.Minute)
}

// Routes 는 HTTP 핸들러를 돌려준다.
func (s *Server) Routes() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("GET /healthz", s.handleHealth)
	mux.HandleFunc("GET /readyz", s.handleReady)
	mux.HandleFunc("GET /metrics", s.handleMetrics)
	mux.HandleFunc("POST /audit", s.handleAudit)
	mux.HandleFunc("POST /session", s.handleCreateSession)
	mux.HandleFunc("GET /ws", s.handleWS)
	return SecurityHeaders(mux)
}

func (s *Server) handleHealth(w http.ResponseWriter, _ *http.Request) {
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write([]byte("ok"))
}

type createSessionResponse struct {
	SessionID string `json:"sessionId"`
	Code      string `json:"code"`
	HostToken string `json:"hostToken"`
	ExpiresIn int    `json:"expiresIn"`
}

// readFixedCode returns the Host's optional stable device code ("" for a random one).
func readFixedCode(r *http.Request) (string, error) {
	if r.Body == nil || r.ContentLength == 0 {
		return "", nil
	}
	var body struct {
		Code string `json:"code"`
	}
	if err := json.NewDecoder(io.LimitReader(r.Body, 256)).Decode(&body); err != nil {
		return "", err
	}
	if body.Code != "" && !decimalField(body.Code, 9) {
		return "", errors.New("bad code")
	}
	return body.Code, nil
}

// handleCreateSession 은 Host 헬퍼가 지원 대기를 시작할 때 호출한다.
// Host는 고정 지원 ID를 보내 같은 방 번호를 계속 쓴다.
func (s *Server) handleCreateSession(w http.ResponseWriter, r *http.Request) {
	if !sameOrigin(r) {
		http.Error(w, "Origin 거부", http.StatusForbidden)
		return
	}
	fixedCode, err := readFixedCode(r)
	if err != nil {
		http.Error(w, "device code 오류", http.StatusBadRequest)
		return
	}
	rm, err := s.rooms.Create(fixedCode)
	if err != nil {
		s.log.Error("세션 생성 실패", "kind", "room-create")
		status := http.StatusInternalServerError
		if errors.Is(err, room.ErrCapacity) {
			status = http.StatusServiceUnavailable
		}
		if errors.Is(err, room.ErrSlotTaken) {
			status = http.StatusConflict
		}
		http.Error(w, "세션 생성 실패", status)
		return
	}

	s.log.Info("세션 생성", "sessionId", rm.SessionID, "ttl", s.cfg.SessionTTL)

	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(createSessionResponse{
		SessionID: rm.SessionID,
		Code:      rm.Code,
		HostToken: rm.HostToken,
		ExpiresIn: int(s.cfg.SessionTTL.Seconds()),
	})
}

// wsConn 은 websocket 연결을 room.Conn 으로 감싼다.
//
// coder/websocket 은 동시 쓰기를 허용하지 않는다. 반대편 피어의 읽기 고루틴에서도
// 이 연결에 쓰기 때문에 뮤텍스로 직렬화한다. 고루틴을 하나 더 띄우는 대신
// 뮤텍스를 쓰는 이유는, 세는 고루틴이 이 프로젝트에서 가장 흔한 실수이기 때문이다.
type wsConn struct {
	c  *websocket.Conn
	mu chan struct{} // 크기 1 채널을 뮤텍스로 쓴다(ctx로 취소 가능한 락)
}

func newWSConn(c *websocket.Conn) *wsConn {
	return &wsConn{c: c, mu: make(chan struct{}, 1)}
}

func (w *wsConn) WriteJSON(ctx context.Context, v any) error {
	if m, ok := v.(Msg); ok {
		m.Version = 1
		v = m
	}
	select {
	case w.mu <- struct{}{}:
		defer func() { <-w.mu }()
	case <-ctx.Done():
		return ctx.Err()
	}

	ctx, cancel := context.WithTimeout(ctx, writeTimeout)
	defer cancel()
	return wsjson.Write(ctx, w.c, v)
}

func (s *Server) handleWS(w http.ResponseWriter, r *http.Request) {
	s.mu.Lock()
	if s.stopping || len(s.connections)+s.pending >= 2000 {
		s.mu.Unlock()
		http.Error(w, "접속 상한", http.StatusServiceUnavailable)
		return
	}
	s.pending++
	s.mu.Unlock()
	// Capacity is reserved; slow upgrade never holds the server mutex.
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		OriginPatterns: s.cfg.OriginPatterns,
	})
	if err != nil {
		s.mu.Lock()
		s.pending--
		s.mu.Unlock()
		s.log.Warn("WebSocket 수락 실패", "kind", "upgrade-rejected")
		return
	}
	s.mu.Lock()
	s.pending--
	if s.stopping {
		s.mu.Unlock()
		_ = c.CloseNow()
		return
	}
	s.connections[c] = struct{}{}
	s.mu.Unlock()
	defer func() { s.mu.Lock(); delete(s.connections, c); s.mu.Unlock() }()
	defer c.CloseNow() //nolint:errcheck // 정상 종료 경로에서는 이미 닫혀 있다

	c.SetReadLimit(readLimit)
	conn := newWSConn(c)
	ctx := r.Context()

	// 1) 첫 메시지는 반드시 join 이다.
	joinCtx, cancel := context.WithTimeout(ctx, joinTimeout)
	var first Msg
	err = readMsg(joinCtx, c, &first)
	cancel()
	if err != nil {
		s.closeWith(c, websocket.StatusPolicyViolation, "join 대기 시간 초과")
		return
	}
	if first.Type != "join" {
		s.reject(ctx, conn, c, "bad-request", "첫 메시지는 join이어야 합니다")
		return
	}

	// 2) 룸 확인. Host만 토큰으로 자기 방을 증명한다. Viewer는 지원 ID만으로 들어온다.
	if !decimalField(first.Room, 9) {
		s.reject(ctx, conn, c, "bad-request", "참가 필드 오류")
		return
	}
	peer := &room.Peer{ID: peerID(r), Role: first.Role, DisplayName: displayName(first.DisplayName), Conn: conn,
		Kick: func() {
			_ = conn.WriteJSON(context.Background(), errMsg("replaced", "다른 지원자 화면이 접속을 이어받았다"))
			_ = c.CloseNow()
		}}
	rm, err := s.rooms.Reserve(first.Room, first.Role, first.Token, peer)
	if errors.Is(err, room.ErrSlotTaken) && first.Role == room.RoleViewer {
		// 새 Viewer가 접속을 이어받는다. 기존 Viewer를 내보내면 Host가 새 지원 대기를 다시 연다.
		if old := s.rooms.Viewer(first.Room); old != nil && old.Kick != nil {
			old.Kick()
		}
		s.reject(ctx, conn, c, "busy", "이전 연결을 정리하는 중이다")
		return
	}
	if err != nil {
		s.reject(ctx, conn, c, errCode(err), err.Error())
		return
	}
	defer s.leave(rm, peer)

	// 3) 참가 알림.
	other := rm.Other(peer)
	present := other != nil
	if s.beforeACK != nil {
		s.beforeACK(peer)
	}
	if err := conn.WriteJSON(ctx, Msg{
		Type: "joined", Room: rm.Code, Role: peer.Role, PeerPresent: &present,
		SessionID: rm.SessionID, ConnectionID: peer.ID, Generation: 1,
	}); err != nil {
		return
	}
	other, err = s.rooms.Activate(rm, peer)
	if err != nil {
		return
	}
	if other != nil {
		// 기존 참가자에게 알린다. Host는 이 신호를 받고 offer를 만든다.
		if err := other.Conn.WriteJSON(ctx, Msg{Type: "peer-joined", Role: peer.Role, DisplayName: peer.DisplayName, ConnectionID: peer.ID, SessionID: rm.SessionID, Generation: 1}); err != nil {
			return
		}
		if !present || peer.Role == room.RoleHost {
			if err := conn.WriteJSON(ctx, Msg{Type: "peer-joined", Role: other.Role, DisplayName: other.DisplayName, ConnectionID: other.ID, SessionID: rm.SessionID, Generation: 1}); err != nil {
				return
			}
		}
	}

	s.log.Info("참가", "sessionId", rm.SessionID, "role", peer.Role, "peerPresent", present)
	heartbeatCtx, stopHeartbeat := context.WithCancel(ctx)
	defer stopHeartbeat()
	go func() {
		t := time.NewTicker(s.heartbeatInterval)
		defer t.Stop()
		for {
			select {
			case <-heartbeatCtx.Done():
				return
			case <-t.C:
				if err := conn.WriteJSON(heartbeatCtx, Msg{Type: "ping"}); err != nil {
					_ = c.CloseNow()
					return
				}
			}
		}
	}()

	// 4) 릴레이 루프.
	for {
		var m Msg
		readCtx, cancelRead := context.WithTimeout(ctx, s.readLease)
		err := readMsg(readCtx, c, &m)
		cancelRead()
		if err != nil {
			return // 정상 종료·연결 끊김 모두 여기로 온다. defer가 정리한다.
		}

		switch m.Type {
		case "ping":
			if err := conn.WriteJSON(ctx, Msg{Type: "pong"}); err != nil {
				return
			}
		case "pong":
		case "offer", "answer", "ice", "bye":
			generation, valid := rm.Negotiation(peer.Role, m.Type, m.Generation)
			if !valid {
				if m.Generation < generation {
					continue
				}
				return
			}
			if m.Type == "offer" && peer.Role != room.RoleHost || m.Type == "answer" && peer.Role != room.RoleViewer {
				return
			}
			if len(m.SDP) > 96<<10 || (m.ICE != nil && len(m.ICE.Candidate) > 8192) {
				return
			}
			if p := rm.Other(peer); p != nil {
				// 인증 필드(token)를 떼고 필요한 것만 넘긴다.
				if err := p.Conn.WriteJSON(ctx, Msg{
					Type:      m.Type,
					Room:      rm.Code,
					SDP:       m.SDP,
					ICE:       m.ICE,
					SessionID: rm.SessionID, Generation: generation,
				}); err != nil {
					return
				}
			}
			if m.Type == "bye" {
				return
			}
		case "join":
			_ = conn.WriteJSON(ctx, errMsg("bad-request", "이미 참가한 연결입니다"))
		default:
			s.reject(ctx, conn, c, "bad-request", "알 수 없는 메시지")
			return
		}
	}
}

func decimalField(value string, size int) bool {
	if len(value) != size {
		return false
	}
	for _, character := range value {
		if character < '0' || character > '9' {
			return false
		}
	}
	return true
}

// displayName 은 표시용 이름을 다듬는다. 이름은 신원 확인에 쓰지 않으므로 거부하지 않는다.
func displayName(value string) string {
	var out []rune
	for _, character := range strings.TrimSpace(value) {
		if unicode.IsControl(character) {
			continue
		}
		if len(utf16.Encode(append(out, character))) > 80 {
			break
		}
		out = append(out, character)
	}
	if len(out) == 0 {
		return "지원자"
	}
	return string(out)
}

// leave 는 피어를 룸에서 빼고, 남은 쪽에 알리고, 빈 룸을 지운다.
//
// 이 함수가 룸 누수를 막는 유일한 지점이다. 어떤 경로로 끊기든 반드시 지나가야 한다.
func (s *Server) leave(rm *room.Room, p *room.Peer) {
	other := s.rooms.Leave(rm, p)

	if other != nil {
		ctx, cancel := context.WithTimeout(context.Background(), notifyTimeout)
		_ = other.Conn.WriteJSON(ctx, Msg{Type: "peer-left", Role: p.Role})
		cancel()
	}

	if rm.Empty() {
		s.log.Info("룸 정리", "sessionId", rm.SessionID, "남은 룸", s.rooms.Count())
	}
}

// reject 는 오류를 알리고 연결을 닫는다.
func (s *Server) reject(ctx context.Context, conn *wsConn, c *websocket.Conn, code, reason string) {
	s.failures.Add(1)
	_ = conn.WriteJSON(ctx, errMsg(code, reason))
	s.closeWith(c, websocket.StatusPolicyViolation, code)
}

func (s *Server) closeWith(c *websocket.Conn, status websocket.StatusCode, reason string) {
	_ = c.Close(status, reason)
}

func errMsg(code, reason string) Msg {
	return Msg{Type: "error", Code: code, Reason: reason}
}

// errCode 는 room 패키지의 에러를 docs/README.md#protocol 의 오류 코드로 옮긴다.
func errCode(err error) string {
	switch {
	case errors.Is(err, room.ErrNotFound):
		return "not-found"
	case errors.Is(err, room.ErrExpired):
		return "expired"
	case errors.Is(err, room.ErrBadToken):
		return "bad-token"
	case errors.Is(err, room.ErrBadRole):
		return "bad-role"
	case errors.Is(err, room.ErrSlotTaken):
		return "slot-taken"
	case errors.Is(err, room.ErrConsumed):
		return "consumed"
	case errors.Is(err, room.ErrCapacity):
		return "capacity"
	default:
		return "bad-request"
	}
}

// peerID 는 로그용 식별자다. 보안 목적이 아니다.
func peerID(r *http.Request) string {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		panic("OS random source unavailable")
	}
	return hex.EncodeToString(b)
}

// Shutdown closes hijacked WebSockets, which http.Server.Shutdown does not own.
func (s *Server) Shutdown() {
	s.mu.Lock()
	s.stopping = true
	peers := make([]*websocket.Conn, 0, len(s.connections))
	for c := range s.connections {
		peers = append(peers, c)
	}
	s.mu.Unlock()
	for _, c := range peers {
		_ = c.CloseNow()
	}
}
