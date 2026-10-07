package signaling

import (
	"bytes"
	"crypto/hmac"
	"crypto/sha1"
	"encoding/base64"
	"encoding/json"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"
)

func TestGoldenFixtures(t *testing.T) {
	data, err := os.ReadFile("../../../../tests/fixtures/signals.json")
	if err != nil {
		t.Fatal(err)
	}
	var fixtures []struct {
		Name    string
		Valid   bool
		Message json.RawMessage
	}
	if err := json.Unmarshal(data, &fixtures); err != nil {
		t.Fatal(err)
	}
	for _, fixture := range fixtures {
		t.Run(fixture.Name, func(t *testing.T) {
			var message Msg
			err := validateJSON(json.NewDecoder(bytes.NewReader(fixture.Message)))
			if err == nil {
				err = json.Unmarshal(fixture.Message, &message)
			}
			valid := err == nil && message.Version == 1 && message.Type != ""
			if valid != fixture.Valid {
				t.Fatal("fixture acceptance mismatch")
			}
			if valid {
				encoded, err := json.Marshal(message)
				if err != nil {
					t.Fatal(err)
				}
				var again Msg
				if err = json.Unmarshal(encoded, &again); err != nil || again.Type != message.Type || again.SessionID != message.SessionID || again.Reason != message.Reason {
					t.Fatal("roundtrip changed")
				}
			}
		})
	}
}
func TestTURNHMACExpiryAndBinding(t *testing.T) {
	secret := strings.Repeat("s", 32)
	s := New(Config{TURNSecret: secret, TURNURLs: []string{"turn:example.invalid:25010"}}, nil)
	now := time.Unix(1000, 0)
	first := s.iceServers("session1", "peer1", now)[0]
	second := s.iceServers("session2", "peer1", now)[0]
	if first.Username != "1600:session1:peer1" || first.Credential == second.Credential {
		t.Fatal("credential not bound to session or expiry")
	}
	mac := hmac.New(sha1.New, []byte(secret))
	_, _ = mac.Write([]byte(first.Username))
	if first.Credential != base64.StdEncoding.EncodeToString(mac.Sum(nil)) {
		t.Fatal("HMAC mismatch")
	}
}
func TestIssuanceRateAndOrigin(t *testing.T) {
	s, ts := newTestServer(t)
	s.issuance = newLimiter(1, time.Minute, 4096)
	createSession(t, ts)
	response, err := http.Post(ts.URL+"/session", "application/json", nil)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	if response.StatusCode != 429 {
		t.Fatal("rate limit failed")
	}
	request, _ := http.NewRequest("POST", ts.URL+"/session", nil)
	request.Header.Set("Origin", "https://attacker.invalid")
	response2, err := http.DefaultClient.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	defer response2.Body.Close()
	if response2.StatusCode != 403 {
		t.Fatal("cross-origin creation accepted")
	}
}
func TestLimiterMapIsBounded(t *testing.T) {
	limiter := newLimiter(1, time.Minute, 2)
	if !limiter.Allow("a") || !limiter.Allow("b") || limiter.Allow("c") || len(limiter.entries) != 2 {
		t.Fatal("unbounded rate map")
	}
}
func TestJSONDepthMatchesProtocol32(t *testing.T) {
	for _, nested := range []int{31, 32} {
		data := `{"v":1,"type":"ping","extension":` + strings.Repeat(`{"x":`, nested) + `0` + strings.Repeat(`}`, nested) + `}`
		err := validateJSON(json.NewDecoder(strings.NewReader(data)))
		if (err == nil) != (nested == 31) {
			t.Fatal("depth boundary differs from v1 contract")
		}
	}
}
