// Package signaling 은 시그널링 HTTP/WebSocket 엔드포인트를 제공한다.
//
// 서버가 하는 일은 두 가지뿐이다.
//  1. 세션 코드·PIN 발급 (POST /session)
//  2. 같은 룸의 두 피어 사이에서 SDP/ICE를 그대로 옮겨 주기 (GET /ws)
//
// 미디어는 여기를 거치지 않는다. 협상이 끝나면 Host와 Viewer가 직접(또는 TURN 경유로)
// 연결하고, 서버는 더 이상 관여하지 않는다.
//
// 와이어 포맷의 기준은 docs/protocol.md 다. 이 파일이 아니다.
package signaling

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"errors"
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
	"github.com/remoteassist/signaling/internal/room"
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

// Msg 는 시그널링 메시지다. 필드명 기준은 docs/protocol.md.
type Msg struct {
	ICEServers           []ICEServer `json:"iceServers,omitempty"`
	Version              int         `json:"v"`
	SessionID            string      `json:"sessionId,omitempty"`
	ConnectionID         string      `json:"connectionId,omitempty"`
	Generation           int         `json:"generation,omitempty"`
	PermissionGeneration int         `json:"permissionGeneration,omitempty"`
	Permissions          []string    `json:"permissions,omitempty"`
	DisplayName          string      `json:"displayName,omitempty"`
	Type                 string      `json:"type"`
	Room                 string      `json:"room,omitempty"`
	Role                 string      `json:"role,omitempty"`

	// 참가 인증용. 절대 반대편으로 릴레이하지 않는다.
	PIN   string `json:"pin,omitempty"`
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
	OriginPatterns     []string
	TURNURLs           []string
	TURNSecret         string
	TURNSecretFile     string
	OperatorSecretFile string
	AuditDirectory     string
}

// Server 는 시그널링 서버다.
type Server struct {
	rooms             *room.Manager
	cfg               Config
	log               *slog.Logger
	mu                sync.Mutex
	connections       map[*websocket.Conn]struct{}
	stopping          bool
	pending           int
	issuance          *requestLimiter
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
		issuance:          newLimiter(10, time.Minute, 4096),
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
	PIN       string `json:"pin"`
	HostToken string `json:"hostToken"`
	ExpiresIn int    `json:"expiresIn"`
}

// handleCreateSession 은 Host 앱이 "세션 시작"을 눌렀을 때 호출한다.
//
// Out-of-band pairing is the selected identity model; issuance is bounded per source IP.
func (s *Server) handleCreateSession(w http.ResponseWriter, r *http.Request) {
	if !sameOrigin(r) {
		http.Error(w, "Origin 거부", http.StatusForbidden)
		return
	}
	if !s.issuance.Allow(remoteIP(r)) {
		http.Error(w, "발급 요청 상한", http.StatusTooManyRequests)
		return
	}
	rm, err := s.rooms.Create()
	if err != nil {
		s.log.Error("세션 생성 실패", "kind", "room-create")
		status := http.StatusInternalServerError
		if errors.Is(err, room.ErrCapacity) {
			status = http.StatusServiceUnavailable
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
		PIN:       rm.PIN,
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

	// 2) 룸 확인과 참가 인증.
	if !decimalField(first.Room, 9) || (first.Role == room.RoleViewer && (!decimalField(first.PIN, 4) || !validDisplayName(first.DisplayName))) {
		s.reject(ctx, conn, c, "bad-request", "참가 필드 오류")
		return
	}
	peer := &room.Peer{ID: peerID(r), Role: first.Role, DisplayName: first.DisplayName, Conn: conn}
	rm, err := s.rooms.Reserve(first.Room, first.Role, first.PIN, first.Token, peer)
	if err != nil {
		s.reject(ctx, conn, c, errCode(err), err.Error())
		return
	}
	defer s.leave(rm, peer)

	// 3) 참가 알림.
	iceServers := s.iceServers(rm.SessionID, peer.ID, time.Now())
	if len(s.cfg.TURNURLs) > 0 && len(iceServers) == 0 {
		s.reject(ctx, conn, c, "unavailable", "TURN key unavailable")
		return
	}
	other := rm.Other(peer)
	present := other != nil
	if s.beforeACK != nil {
		s.beforeACK(peer)
	}
	if err := conn.WriteJSON(ctx, Msg{
		Type: "joined", Room: rm.Code, Role: peer.Role, PeerPresent: &present,
		SessionID: rm.SessionID, ConnectionID: peer.ID, Generation: 1,
		ICEServers: iceServers,
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
		case "turn-refresh":
			if !rm.Refresh(peer.Role, time.Now()) {
				return
			}
			p := rm.Other(peer)
			if p == nil {
				return
			}
			local := s.iceServers(rm.SessionID, peer.ID, time.Now())
			remote := s.iceServers(rm.SessionID, p.ID, time.Now())
			if len(s.cfg.TURNURLs) > 0 && (len(local) == 0 || len(remote) == 0) {
				return
			}
			gen, ok := rm.Negotiation(peer.Role, "ice", m.Generation)
			if !ok {
				return
			}
			if err := p.Conn.WriteJSON(ctx, Msg{Type: "turn-credentials", SessionID: rm.SessionID, Generation: gen, ICEServers: remote}); err != nil {
				return
			}
			if err := conn.WriteJSON(ctx, Msg{Type: "turn-credentials", SessionID: rm.SessionID, Generation: gen, ICEServers: local}); err != nil {
				return
			}
		case "ping":
			if err := conn.WriteJSON(ctx, Msg{Type: "pong"}); err != nil {
				return
			}
		case "pong":
		case "offer", "answer", "ice", "bye", "consent", "consent-pending", "consent-denied":
			generation, valid := rm.Negotiation(peer.Role, m.Type, m.Generation)
			if !valid {
				if m.Generation < generation {
					continue
				}
				return
			}
			if (m.Type == "offer" || m.Type == "consent" || m.Type == "consent-pending" || m.Type == "consent-denied") && peer.Role != room.RoleHost {
				return
			}
			if m.Type == "answer" && peer.Role != room.RoleViewer {
				return
			}
			if m.Type == "consent" {
				if _, ok := permissionMask(m.Permissions); !ok || m.PermissionGeneration != 1 {
					return
				}
			}
			if len(m.SDP) > 96<<10 || (m.ICE != nil && len(m.ICE.Candidate) > 8192) {
				return
			}
			if p := rm.Other(peer); p != nil {
				// 인증 필드(pin/token)를 떼고 필요한 것만 넘긴다.
				if err := p.Conn.WriteJSON(ctx, Msg{
					Type:      m.Type,
					Room:      rm.Code,
					SDP:       m.SDP,
					ICE:       m.ICE,
					SessionID: rm.SessionID, Generation: generation, PermissionGeneration: m.PermissionGeneration, Permissions: m.Permissions,
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
func validDisplayName(value string) bool {
	if strings.TrimSpace(value) != value || value == "" || len(utf16.Encode([]rune(value))) > 80 {
		return false
	}
	for _, character := range value {
		if unicode.IsControl(character) {
			return false
		}
	}
	return true
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

// errCode 는 room 패키지의 에러를 docs/protocol.md 의 오류 코드로 옮긴다.
func errCode(err error) string {
	switch {
	case errors.Is(err, room.ErrNotFound):
		return "not-found"
	case errors.Is(err, room.ErrExpired):
		return "expired"
	case errors.Is(err, room.ErrBadPIN):
		return "bad-pin"
	case errors.Is(err, room.ErrBadToken):
		return "bad-token"
	case errors.Is(err, room.ErrBadRole):
		return "bad-role"
	case errors.Is(err, room.ErrSlotTaken):
		return "slot-taken"
	case errors.Is(err, room.ErrConsumed):
		return "consumed"
	case errors.Is(err, room.ErrLocked):
		return "locked"
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
