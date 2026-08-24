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
	"encoding/json"
	"errors"
	"log/slog"
	"net/http"
	"time"

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
	Type string `json:"type"`
	Room string `json:"room,omitempty"`
	Role string `json:"role,omitempty"`

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
	OriginPatterns []string
}

// Server 는 시그널링 서버다.
type Server struct {
	rooms *room.Manager
	cfg   Config
	log   *slog.Logger
}

// New 는 서버를 만든다.
func New(cfg Config, log *slog.Logger) *Server {
	if cfg.SessionTTL <= 0 {
		cfg.SessionTTL = 10 * time.Minute
	}
	if log == nil {
		log = slog.Default()
	}
	return &Server{
		rooms: room.NewManager(cfg.SessionTTL),
		cfg:   cfg,
		log:   log,
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
	mux.HandleFunc("POST /session", s.handleCreateSession)
	mux.HandleFunc("GET /ws", s.handleWS)
	return mux
}

func (s *Server) handleHealth(w http.ResponseWriter, _ *http.Request) {
	w.WriteHeader(http.StatusOK)
	_, _ = w.Write([]byte("ok"))
}

type createSessionResponse struct {
	Code      string `json:"code"`
	PIN       string `json:"pin"`
	HostToken string `json:"hostToken"`
	ExpiresIn int    `json:"expiresIn"`
}

// handleCreateSession 은 Host 앱이 "세션 시작"을 눌렀을 때 호출한다.
//
// TODO(T3.1): 발급 자체에 대한 인증·레이트 리밋. 지금은 누구나 코드를 받을 수 있다.
// 코드만으로는 아무것도 못 하지만(참가에 PIN/토큰이 필요하다), 남용 방지는 필요하다.
func (s *Server) handleCreateSession(w http.ResponseWriter, r *http.Request) {
	rm, err := s.rooms.Create()
	if err != nil {
		s.log.Error("세션 생성 실패", "err", err)
		http.Error(w, "세션 생성 실패", http.StatusInternalServerError)
		return
	}

	s.log.Info("세션 생성", "code", rm.Code, "ttl", s.cfg.SessionTTL)

	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.Header().Set("Cache-Control", "no-store")
	_ = json.NewEncoder(w).Encode(createSessionResponse{
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
	c, err := websocket.Accept(w, r, &websocket.AcceptOptions{
		OriginPatterns: s.cfg.OriginPatterns,
	})
	if err != nil {
		s.log.Warn("WebSocket 수락 실패", "err", err)
		return
	}
	defer c.CloseNow() //nolint:errcheck // 정상 종료 경로에서는 이미 닫혀 있다

	c.SetReadLimit(readLimit)
	conn := newWSConn(c)
	ctx := r.Context()

	// 1) 첫 메시지는 반드시 join 이다.
	joinCtx, cancel := context.WithTimeout(ctx, joinTimeout)
	var first Msg
	err = wsjson.Read(joinCtx, c, &first)
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
	rm, err := s.rooms.Get(first.Room)
	if err != nil {
		s.reject(ctx, conn, c, errCode(err), err.Error())
		return
	}
	if err := rm.Authorize(first.Role, first.PIN, first.Token); err != nil {
		s.log.Warn("참가 인증 실패", "code", rm.Code, "role", first.Role, "err", err)
		s.reject(ctx, conn, c, errCode(err), err.Error())
		return
	}

	peer := &room.Peer{ID: peerID(r), Role: first.Role, Conn: conn}
	if err := rm.Join(peer); err != nil {
		s.reject(ctx, conn, c, errCode(err), err.Error())
		return
	}
	defer s.leave(rm, peer)

	// 3) 참가 알림.
	other := rm.Other(peer)
	present := other != nil
	if err := conn.WriteJSON(ctx, Msg{
		Type: "joined", Room: rm.Code, Role: peer.Role, PeerPresent: &present,
	}); err != nil {
		return
	}
	if other != nil {
		// 기존 참가자에게 알린다. Host는 이 신호를 받고 offer를 만든다.
		_ = other.Conn.WriteJSON(ctx, Msg{Type: "peer-joined", Role: peer.Role})
	}

	s.log.Info("참가", "code", rm.Code, "role", peer.Role, "peerPresent", present)

	// 4) 릴레이 루프.
	for {
		var m Msg
		if err := wsjson.Read(ctx, c, &m); err != nil {
			return // 정상 종료·연결 끊김 모두 여기로 온다. defer가 정리한다.
		}

		switch m.Type {
		case "offer", "answer", "ice", "bye":
			if p := rm.Other(peer); p != nil {
				// 인증 필드(pin/token)를 떼고 필요한 것만 넘긴다.
				_ = p.Conn.WriteJSON(ctx, Msg{
					Type: m.Type,
					Room: rm.Code,
					SDP:  m.SDP,
					ICE:  m.ICE,
				})
			}
			if m.Type == "bye" {
				return
			}
		case "join":
			_ = conn.WriteJSON(ctx, errMsg("bad-request", "이미 참가한 연결입니다"))
		default:
			_ = conn.WriteJSON(ctx, errMsg("bad-request", "알 수 없는 type: "+m.Type))
		}
	}
}

// leave 는 피어를 룸에서 빼고, 남은 쪽에 알리고, 빈 룸을 지운다.
//
// 이 함수가 룸 누수를 막는 유일한 지점이다. 어떤 경로로 끊기든 반드시 지나가야 한다.
func (s *Server) leave(rm *room.Room, p *room.Peer) {
	other := rm.Other(p)
	rm.Leave(p)

	if other != nil {
		ctx, cancel := context.WithTimeout(context.Background(), notifyTimeout)
		_ = other.Conn.WriteJSON(ctx, Msg{Type: "peer-left", Role: p.Role})
		cancel()
	}

	if rm.Empty() {
		s.rooms.Remove(rm.Code)
		s.log.Info("룸 정리", "code", rm.Code, "남은 룸", s.rooms.Count())
	}
}

// reject 는 오류를 알리고 연결을 닫는다.
func (s *Server) reject(ctx context.Context, conn *wsConn, c *websocket.Conn, code, reason string) {
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
	default:
		return "bad-request"
	}
}

// peerID 는 로그용 식별자다. 보안 목적이 아니다.
func peerID(r *http.Request) string {
	return r.RemoteAddr
}
