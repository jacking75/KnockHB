package signaling

import (
	"bytes"
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
func TestCrossOriginSessionCreationRejected(t *testing.T) {
	_, ts := newTestServer(t)
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
