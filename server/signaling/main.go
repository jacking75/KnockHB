// RemoteAssist 시그널링 서버.
//
// 두 피어는 서로의 주소를 모른 채 시작한다. 이 서버가 세션 코드를 매개로 둘을
// 같은 룸에 묶고, WebRTC 협상에 필요한 SDP와 ICE 후보만 오가게 한다.
// 협상이 끝나면 미디어·데이터는 서버를 거치지 않는다.
//
// 포트는 전역 개발 포트 대역(TCP 25001~25199) 안에서 25001을 기본으로 쓴다.
//
// 환경 변수:
//
//	RA_LISTEN          리슨 주소          (기본 :25001)
//	RA_SESSION_TTL     세션 코드 유효기간 (기본 10m)
//	RA_ALLOWED_ORIGINS 브라우저 뷰어 허용 Origin, 쉼표 구분 (기본 없음 = 동일 호스트만)
//	RA_VIEWER_DIR      웹 뷰어 정적 파일 디렉터리. 지정하면 / 로 서빙한다 (개발용)
package main

import (
	"context"
	"errors"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/remoteassist/signaling/internal/signaling"
)

func main() {
	log := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))

	cfg := signaling.Config{
		SessionTTL:     envDuration("RA_SESSION_TTL", 10*time.Minute),
		OriginPatterns: envList("RA_ALLOWED_ORIGINS"),
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	srv := signaling.New(cfg, log)
	srv.StartSweeper(ctx)

	handler := srv.Routes()

	// 개발 편의: 웹 뷰어를 같은 서버에서 띄우면 Origin이 같아져 WebSocket 검사를 통과한다.
	if dir := os.Getenv("RA_VIEWER_DIR"); dir != "" {
		mux := http.NewServeMux()
		mux.Handle("/", http.FileServer(http.Dir(dir)))
		mux.Handle("/healthz", handler)
		mux.Handle("/session", handler)
		mux.Handle("/ws", handler)
		handler = mux
		log.Info("웹 뷰어 정적 서빙", "dir", dir)
	}

	addr := env("RA_LISTEN", ":25001")
	httpSrv := &http.Server{
		Addr:              addr,
		Handler:           handler,
		ReadHeaderTimeout: 10 * time.Second,
		// WebSocket은 오래 붙어 있으므로 WriteTimeout을 걸지 않는다.
	}

	go func() {
		log.Info("시그널링 서버 시작", "addr", addr, "sessionTTL", cfg.SessionTTL)
		if err := httpSrv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("listen 실패", "err", err)
			stop()
		}
	}()

	<-ctx.Done()
	log.Info("종료 신호 수신, 정리 중")

	shutdownCtx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	if err := httpSrv.Shutdown(shutdownCtx); err != nil {
		log.Error("종료 중 오류", "err", err)
	}
	log.Info("시그널링 서버 종료")
}

func env(key, def string) string {
	if v := os.Getenv(key); v != "" {
		return v
	}
	return def
}

func envDuration(key string, def time.Duration) time.Duration {
	v := os.Getenv(key)
	if v == "" {
		return def
	}
	d, err := time.ParseDuration(v)
	if err != nil {
		return def
	}
	return d
}

func envList(key string) []string {
	v := os.Getenv(key)
	if v == "" {
		return nil
	}
	parts := strings.Split(v, ",")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		if p = strings.TrimSpace(p); p != "" {
			out = append(out, p)
		}
	}
	return out
}
