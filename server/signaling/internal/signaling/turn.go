package signaling

import (
	"crypto/hmac"
	"crypto/sha1"
	"encoding/base64"
	"fmt"
	"time"
)

type ICEServer struct {
	URLs       []string `json:"urls"`
	Username   string   `json:"username,omitempty"`
	Credential string   `json:"credential,omitempty"`
}

func (s *Server) iceServers(sessionID, connectionID string, now time.Time) []ICEServer {
	if len(s.cfg.TURNURLs) == 0 || len(s.cfg.TURNSecret) < 32 {
		return nil
	}
	username := fmt.Sprintf("%d:%s:%s", now.Add(10*time.Minute).Unix(), sessionID, connectionID)
	// coturn REST credentials mandate HMAC-SHA1; this is not password hashing.
	mac := hmac.New(sha1.New, []byte(s.cfg.TURNSecret))
	_, _ = mac.Write([]byte(username))
	return []ICEServer{{URLs: s.cfg.TURNURLs, Username: username, Credential: base64.StdEncoding.EncodeToString(mac.Sum(nil))}}
}
