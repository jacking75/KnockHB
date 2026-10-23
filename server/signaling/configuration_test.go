package main

import "testing"

func TestInvalidConfigNeverSilentlyFallsBack(t *testing.T) {
	for _, key := range []string{"RA_MODE", "RA_LISTEN", "RA_SESSION_TTL", "RA_TLS_CERT", "RA_TLS_KEY", "RA_VIEWER_DIR", "RA_ALLOWED_ORIGINS", "RA_OPERATOR_SECRET_FILE", "RA_AUDIT_DIR"} {
		t.Setenv(key, "")
	}
	for _, test := range []struct{ key, value string }{{"RA_SESSION_TTL", "oops"}, {"RA_SESSION_TTL", "-1m"}, {"RA_LISTEN", ":25001"}, {"RA_LISTEN", "127.0.0.1:8080"}, {"RA_MODE", "production"}, {"RA_MODE", "typo"}, {"RA_ALLOWED_ORIGINS", "*"}, {"RA_MODE", "lan"}} {
		t.Run(test.key+test.value, func(t *testing.T) {
			t.Setenv(test.key, test.value)
			if _, err := loadSettings(); err == nil {
				t.Fatal("invalid configuration accepted")
			}
		})
	}
	if _, err := loadSettings(); err != nil {
		t.Fatal("loopback defaults rejected", err)
	}
}
