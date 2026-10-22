package signaling

import (
	"crypto/hmac"
	"crypto/sha1"
	"encoding/base64"
	"fmt"
	"io"
	"os"
	"runtime"
	"strings"
	"time"
)

type ICEServer struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}

func (s *Server) iceServers(sessionID, connectionID string, now time.Time) []ICEServer {
	var result []ICEServer
	if len(s.cfg.STUNURLs) > 0 {
		result = append(result, ICEServer{URLs: s.cfg.STUNURLs})
	}
	secret := s.turnSecret()
	if len(s.cfg.TURNURLs) == 0 || len(secret) < 32 {
		return result
	}
	username := fmt.Sprintf("%d:%s:%s", now.Add(10*time.Minute).Unix(), sessionID, connectionID)
	// coturn REST credentials mandate HMAC-SHA1; this is not password hashing.
	mac := hmac.New(sha1.New, []byte(secret))
	_, _ = mac.Write([]byte(username))
	return append(result, ICEServer{URLs: s.cfg.TURNURLs, Username: username, Credential: base64.StdEncoding.EncodeToString(mac.Sum(nil))})
}
func (s *Server) turnSecret() string {
	if s.cfg.TURNSecretFile == "" {
		return s.cfg.TURNSecret
	}
	return ReadSecretFile(s.cfg.TURNSecretFile)
}
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
