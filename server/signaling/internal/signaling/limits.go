package signaling

import (
	"net"
	"net/http"
	"net/url"
	"sync"
	"time"
)

type counter struct {
	start time.Time
	count int
}
type requestLimiter struct {
	mu       sync.Mutex
	entries  map[string]counter
	limit    int
	window   time.Duration
	capacity int
}

func newLimiter(limit int, window time.Duration, capacity int) *requestLimiter {
	return &requestLimiter{entries: map[string]counter{}, limit: limit, window: window, capacity: capacity}
}
func (l *requestLimiter) Allow(key string) bool {
	l.mu.Lock()
	defer l.mu.Unlock()
	now := time.Now()
	entry, ok := l.entries[key]
	if !ok && len(l.entries) >= l.capacity {
		for k, c := range l.entries {
			if now.Sub(c.start) >= l.window {
				delete(l.entries, k)
			}
		}
		if len(l.entries) >= l.capacity {
			return false
		}
	}
	if !ok || now.Sub(entry.start) >= l.window {
		entry = counter{start: now}
	}
	if entry.count >= l.limit {
		return false
	}
	entry.count++
	l.entries[key] = entry
	return true
}
func remoteIP(r *http.Request) string {
	host, _, err := net.SplitHostPort(r.RemoteAddr)
	if err != nil {
		return r.RemoteAddr
	}
	return host
}
func sameOrigin(r *http.Request) bool {
	origin := r.Header.Get("Origin")
	if origin == "" {
		return true
	}
	u, err := url.Parse(origin)
	scheme := "http"
	if r.TLS != nil {
		scheme = "https"
	}
	return err == nil && u.Scheme == scheme && u.Host == r.Host
}
func SecurityHeaders(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Content-Type-Options", "nosniff")
		w.Header().Set("X-Frame-Options", "DENY")
		w.Header().Set("Referrer-Policy", "no-referrer")
		w.Header().Set("Content-Security-Policy", "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; media-src 'self' blob:; frame-ancestors 'none'; base-uri 'none'; form-action 'self'")
		if r.TLS != nil {
			w.Header().Set("Strict-Transport-Security", "max-age=31536000")
		}
		next.ServeHTTP(w, r)
	})
}
func (s *Server) handleReady(w http.ResponseWriter, r *http.Request) {
	if s.startupError != nil || !s.audit.Healthy() {
		http.Error(w, "audit unavailable", http.StatusServiceUnavailable)
		return
	}
	s.mu.Lock()
	stopping := s.stopping
	s.mu.Unlock()
	if stopping {
		http.Error(w, "stopping", http.StatusServiceUnavailable)
		return
	}
	if len(s.cfg.TURNURLs) > 0 && len(s.turnSecret()) < 32 {
		http.Error(w, "TURN key unavailable", http.StatusServiceUnavailable)
		return
	}
	if s.cfg.OperatorSecretFile != "" && ReadSecretFile(s.cfg.OperatorSecretFile) == "" {
		http.Error(w, "node key unavailable", http.StatusServiceUnavailable)
		return
	}
	w.Header().Set("Content-Type", "application/json")
	_, _ = w.Write([]byte("{\"ready\":true}"))
}
