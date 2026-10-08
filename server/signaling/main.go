package main

import (
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"github.com/remoteassist/signaling/internal/signaling"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
)

type settings struct {
	addr, cert, key, viewer, mode string
	cfg                           signaling.Config
}

var viewerAssets = map[string]bool{"index.html": true, "app.css": true, "app.js": true, "connection.js": true, "input.js": true, "input-geometry.js": true, "favicon.svg": true}

func main() { os.Exit(run()) }
func run() int {
	log := slog.New(slog.NewTextHandler(os.Stdout, nil))
	config, err := loadSettings()
	if err != nil {
		log.Error("설정 검사 실패", "reason", err)
		return 1
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	server := signaling.New(config.cfg, log)
	server.StartSweeper(ctx)
	handler := server.Routes()
	if config.viewer != "" {
		mux := http.NewServeMux()
		for _, path := range []string{"/healthz", "/readyz", "/session", "/ws"} {
			mux.Handle(path, handler)
		}
		mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
			if r.Method != "GET" && r.Method != "HEAD" {
				w.WriteHeader(http.StatusMethodNotAllowed)
				return
			}
			name := strings.TrimPrefix(r.URL.Path, "/")
			if name == "" {
				name = "index.html"
			}
			if filepath.Base(name) != name || !viewerAssets[name] {
				http.NotFound(w, r)
				return
			}
			http.ServeFile(w, r, filepath.Join(config.viewer, name))
		})
		handler = signaling.SecurityHeaders(mux)
	}
	httpServer := &http.Server{Addr: config.addr, Handler: handler, ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 16 << 10, TLSConfig: &tls.Config{MinVersion: tls.VersionTLS12}}
	listenResult := make(chan error, 1)
	go func() {
		log.Info("시그널링 시작", "addr", config.addr, "mode", config.mode)
		if config.mode == "production" {
			listenResult <- httpServer.ListenAndServeTLS(config.cert, config.key)
		} else {
			listenResult <- httpServer.ListenAndServe()
		}
	}()
	var listenErr error
	select {
	case <-ctx.Done():
	case listenErr = <-listenResult:
		stop()
	}
	server.Shutdown() // http shutdown does not own hijacked WS connections.
	shutdown, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	err = httpServer.Shutdown(shutdown)
	if err != nil {
		log.Error("HTTP 종료 실패", "reason", err)
		return 1
	}
	if listenErr != nil && !errors.Is(listenErr, http.ErrServerClosed) {
		log.Error("listen 실패", "reason", listenErr)
		return 1
	}
	log.Info("시그널링 종료")
	return 0
}
func loadSettings() (settings, error) {
	out := settings{mode: env("RA_MODE", "development"), addr: env("RA_LISTEN", "127.0.0.1:25001"), cert: os.Getenv("RA_TLS_CERT"), key: os.Getenv("RA_TLS_KEY"), viewer: os.Getenv("RA_VIEWER_DIR")}
	host, portText, err := net.SplitHostPort(out.addr)
	if err != nil {
		return out, errors.New("RA_LISTEN은 host:port 형식이어야 한다")
	}
	port, err := strconv.Atoi(portText)
	if err != nil || port < 25001 || port > 25199 {
		return out, errors.New("TCP listener는 25001~25199여야 한다")
	}
	if out.mode != "development" && out.mode != "production" {
		return out, errors.New("RA_MODE는 development 또는 production이어야 한다")
	}
	if out.mode == "development" {
		ip := net.ParseIP(host)
		if host != "localhost" && (ip == nil || !ip.IsLoopback()) {
			return out, errors.New("개발 평문 서버는 loopback에서만 실행한다")
		}
	}
	ttl, err := time.ParseDuration(env("RA_SESSION_TTL", "10m"))
	if err != nil || ttl < 30*time.Second || ttl > 30*time.Minute {
		return out, errors.New("RA_SESSION_TTL은 30s~30m이어야 한다")
	}
	out.cfg.SessionTTL = ttl
	out.cfg.OriginPatterns = envList("RA_ALLOWED_ORIGINS")
	for _, origin := range out.cfg.OriginPatterns {
		if strings.Contains(origin, "*") {
			return out, errors.New("Origin wildcard는 허용하지 않는다")
		}
	}
	out.cfg.TURNURLs = envList("RA_TURN_URLS")
	for _, url := range out.cfg.TURNURLs {
		if !strings.HasPrefix(url, "turn:") && !strings.HasPrefix(url, "turns:") {
			return out, errors.New("TURN URL scheme 오류")
		}
	}
	if secretPath := os.Getenv("RA_TURN_SECRET_FILE"); secretPath != "" {
		secret, err := os.ReadFile(secretPath)
		if err != nil {
			return out, errors.New("TURN secret 파일을 읽을 수 없다")
		}
		out.cfg.TURNSecret = strings.TrimSpace(string(secret))
		if len(out.cfg.TURNSecret) < 32 {
			return out, errors.New("TURN secret은 최소 32바이트여야 한다")
		}
	}
	if out.mode == "production" {
		if out.cert == "" || out.key == "" {
			return out, errors.New("운영 TLS 인증서와 키가 필요하다")
		}
		if _, err := tls.LoadX509KeyPair(out.cert, out.key); err != nil {
			return out, errors.New("운영 TLS 인증서/키 검사 실패")
		}
		if len(out.cfg.TURNURLs) == 0 || len(out.cfg.TURNSecret) < 32 {
			return out, errors.New("운영 TURN URL과 보호된 secret이 필요하다")
		}
		if out.viewer == "" {
			return out, errors.New("운영 Viewer 디렉터리가 필요하다")
		}
	}
	if out.viewer != "" {
		absolute, err := filepath.Abs(out.viewer)
		if err != nil {
			return out, err
		}
		out.viewer = absolute
		for name := range viewerAssets {
			if info, err := os.Stat(filepath.Join(absolute, name)); err != nil || !info.Mode().IsRegular() {
				return out, fmt.Errorf("Viewer 필수 파일 누락: %s", name)
			}
		}
	}
	return out, nil
}
func env(key, fallback string) string {
	if value := os.Getenv(key); value != "" {
		return value
	}
	return fallback
}
func envList(key string) []string {
	var result []string
	for _, value := range strings.Split(os.Getenv(key), ",") {
		if value = strings.TrimSpace(value); value != "" {
			result = append(result, value)
		}
	}
	return result
}
