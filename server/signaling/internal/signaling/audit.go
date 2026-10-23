package signaling

import (
	"bytes"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"sync"
	"time"
)

type AuditOperation struct {
	ID        string  `json:"Id"`
	BookID    *string `json:"BookId"`
	Hash      *string `json:"Hash"`
	Direction *string `json:"Direction"`
	Size      *int64  `json:"Size"`
}
type AuditEvent struct {
	ID         string          `json:"EventId"`
	UTC        time.Time       `json:"Utc"`
	Kind       int             `json:"Kind"`
	Result     int             `json:"Result"`
	Session    string          `json:"SessionId"`
	Connection string          `json:"ConnectionId"`
	Generation int64           `json:"PermissionGeneration"`
	Operation  *AuditOperation `json:"Operation"`
}

var auditGuid = regexp.MustCompile(`^[0-9a-f]{32}$`)
var auditIdent = regexp.MustCompile(`^[A-Za-z0-9_-]{0,64}$`)
var auditHash = regexp.MustCompile(`^[0-9a-f]{64}$`)

func (e AuditEvent) validate() error {
	if !auditGuid.MatchString(e.ID) || e.UTC.IsZero() || e.Kind < 0 || e.Kind > 11 || e.Result < 0 || e.Result > 4 || !auditIdent.MatchString(e.Session) || !auditIdent.MatchString(e.Connection) || e.Generation < 0 || e.Generation > 9007199254740991 {
		return errors.New("audit envelope")
	}
	if o := e.Operation; o != nil {
		if !auditGuid.MatchString(o.ID) || o.BookID != nil && (*o.BookID != "network-summary" && *o.BookID != "clear-dns-cache" && *o.BookID != "command") || o.Hash != nil && !auditHash.MatchString(*o.Hash) || o.Direction != nil && (*o.Direction != "from-host" && *o.Direction != "to-host") || o.Size != nil && (*o.Size < 0 || *o.Size > 268435456) {
			return errors.New("audit operation")
		}
	}
	return nil
}

type auditSeen struct {
	at   time.Time
	hash [32]byte
}
type AuditStore struct {
	mu        sync.Mutex
	directory string
	seen      map[string]auditSeen
	failure   error
}

func (a *AuditStore) Healthy() bool {
	if a == nil {
		return true
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	return a.failure == nil
}
func OpenAuditStore(directory string) (*AuditStore, error) {
	if directory == "" {
		return nil, nil
	}
	if err := os.MkdirAll(directory, 0700); err != nil {
		return nil, err
	}
	info, err := os.Lstat(directory)
	if err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		return nil, errors.New("audit directory")
	}
	if runtime.GOOS != "windows" && info.Mode().Perm()&0077 != 0 {
		return nil, errors.New("audit permissions")
	}
	store := &AuditStore{directory: directory, seen: make(map[string]auditSeen)}
	for i := 0; i < 4; i++ {
		name := "audit.ndjson"
		if i > 0 {
			name = fmt.Sprintf("audit.%d.ndjson", i)
		}
		path := filepath.Join(directory, name)
		info, err := os.Lstat(path)
		if os.IsNotExist(err) {
			continue
		}
		if err != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 || info.Size() > 4<<20 {
			return nil, errors.New("audit recovery file")
		}
		data, err := os.ReadFile(path)
		if err != nil || len(data) > 4<<20 {
			return nil, errors.New("audit recovery size")
		}
		for _, line := range bytes.Split(data, []byte{10}) {
			if len(line) == 0 {
				continue
			}
			var e AuditEvent
			if len(line) > 4096 || json.Unmarshal(line, &e) != nil || e.validate() != nil {
				return nil, errors.New("audit recovery record")
			}
			canonical, _ := json.Marshal(e)
			hash := sha256.Sum256(canonical)
			if old, ok := store.seen[e.ID]; ok && old.hash != hash {
				return nil, errors.New("audit conflicting duplicate")
			}
			store.seen[e.ID] = auditSeen{time.Now(), hash}
			if len(store.seen) > 65536 {
				return nil, errors.New("audit recovery capacity")
			}
		}
	}
	return store, nil
}
func (a *AuditStore) Append(e AuditEvent) error {
	if err := e.validate(); err != nil {
		return err
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.failure != nil {
		return a.failure
	}
	now := time.Now()
	for id, entry := range a.seen {
		if now.Sub(entry.at) > 7*24*time.Hour {
			delete(a.seen, id)
		}
	}
	canonical, _ := json.Marshal(e)
	hash := sha256.Sum256(canonical)
	if old, ok := a.seen[e.ID]; ok {
		if old.hash != hash {
			return errors.New("audit conflicting duplicate")
		}
		return nil
	}
	if len(a.seen) >= 65536 {
		a.failure = errors.New("audit duplicate index capacity")
		return a.failure
	}
	data, err := json.Marshal(e)
	if err != nil || len(data) > 4096 {
		return errors.New("audit size")
	}
	path := filepath.Join(a.directory, "audit.ndjson")
	info, err := os.Lstat(path)
	if err != nil && !os.IsNotExist(err) {
		a.failure = err
		return err
	}
	if info != nil && (!info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0) {
		a.failure = errors.New("audit file")
		return a.failure
	}
	if info != nil && (info.Size()+int64(len(data)+1) > 4<<20 || now.Sub(info.ModTime()) > 7*24*time.Hour) {
		if err = os.Remove(filepath.Join(a.directory, "audit.3.ndjson")); err != nil && !os.IsNotExist(err) {
			a.failure = err
			return err
		}
		for i := 2; i >= 0; i-- {
			from := "audit.ndjson"
			if i > 0 {
				from = fmt.Sprintf("audit.%d.ndjson", i)
			}
			err = os.Rename(filepath.Join(a.directory, from), filepath.Join(a.directory, fmt.Sprintf("audit.%d.ndjson", i+1)))
			if err != nil && !os.IsNotExist(err) {
				a.failure = err
				return err
			}
		}
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err != nil {
		a.failure = err
		return err
	}
	_, err = file.Write(append(data, 10))
	if err == nil {
		err = file.Sync()
	}
	if closeErr := file.Close(); err == nil {
		err = closeErr
	}
	// The deployed Linux server must persist creation/rotation directory entries before ACK.
	if err == nil && runtime.GOOS != "windows" {
		var directory *os.File
		directory, err = os.Open(a.directory)
		if err == nil {
			err = directory.Sync()
			if closeErr := directory.Close(); err == nil {
				err = closeErr
			}
		}
	}
	if err != nil {
		a.failure = err
		return err
	}
	a.seen[e.ID] = auditSeen{now, hash}
	return nil
}
func (s *Server) operator(r *http.Request) bool {
	expected := ReadSecretFile(s.cfg.OperatorSecretFile)
	header := r.Header.Get("Authorization")
	if !strings.HasPrefix(header, "Bearer ") {
		return false
	}
	actual := strings.TrimPrefix(header, "Bearer ")
	return len(expected) >= 32 && subtle.ConstantTimeCompare([]byte(actual), []byte(expected)) == 1
}
func (s *Server) handleAudit(w http.ResponseWriter, r *http.Request) {
	if !s.operator(r) {
		http.Error(w, "unauthorized", http.StatusUnauthorized)
		return
	}
	if s.audit == nil {
		http.Error(w, "audit unavailable", http.StatusServiceUnavailable)
		return
	}
	if !s.auditRate.Allow(remoteIP(r)) {
		http.Error(w, "audit rate", http.StatusTooManyRequests)
		return
	}
	raw, err := io.ReadAll(io.LimitReader(r.Body, 4097))
	if err != nil || len(raw) > 4096 || validateJSON(json.NewDecoder(bytes.NewReader(raw))) != nil {
		http.Error(w, "audit body", 400)
		return
	}
	var e AuditEvent
	decoder := json.NewDecoder(bytes.NewReader(raw))
	decoder.DisallowUnknownFields()
	if decoder.Decode(&e) != nil || e.validate() != nil {
		http.Error(w, "audit schema", 400)
		return
	}
	if s.audit.Append(e) != nil {
		http.Error(w, "audit storage unavailable", 503)
		return
	}
	w.WriteHeader(http.StatusNoContent)
}
func (s *Server) handleMetrics(w http.ResponseWriter, r *http.Request) {
	if !s.operator(r) {
		http.Error(w, "unauthorized", 401)
		return
	}
	s.mu.Lock()
	peers, pending, stopping := len(s.connections), s.pending, s.stopping
	s.mu.Unlock()
	ready := 1
	if stopping || s.startupError != nil || !s.audit.Healthy() {
		ready = 0
	}
	w.Header().Set("Content-Type", "text/plain; version=0.0.4")
	fmt.Fprintf(w, "remoteassist_rooms %d\nremoteassist_connections %d\nremoteassist_pending %d\nremoteassist_goroutines %d\nremoteassist_ready %d\nremoteassist_rejected_total %d\n", s.rooms.Count(), peers, pending, runtime.NumGoroutine(), ready, s.failures.Load())
}

// ReadSecretFile reads a small bearer secret. An unreadable or malformed file yields "".
func ReadSecretFile(path string) string {
	info, err := os.Lstat(path)
	if err != nil || !info.Mode().IsRegular() || info.Mode()&os.ModeSymlink != 0 || info.Size() > 256 || runtime.GOOS != "windows" && info.Mode().Perm()&0077 != 0 {
		return ""
	}
	file, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer file.Close()
	buffer, err := io.ReadAll(io.LimitReader(file, 4097))
	if err != nil || len(buffer) > 4096 {
		return ""
	}
	value := strings.TrimSpace(string(buffer))
	if len(value) < 32 || len(value) > 128 {
		return ""
	}
	for _, c := range value {
		if !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_') {
			return ""
		}
	}
	return value
}
