package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestViewerMissingInputModuleFailsBeforeListening(t *testing.T) {
	for _, key := range []string{"RA_MODE", "RA_LISTEN", "RA_SESSION_TTL", "RA_TLS_CERT", "RA_TLS_KEY", "RA_TURN_URLS", "RA_TURN_SECRET_FILE", "RA_ALLOWED_ORIGINS"} {
		t.Setenv(key, "")
	}
	directory := t.TempDir()
	t.Setenv("RA_VIEWER_DIR", directory)
	for name := range viewerAssets {
		if name == "input.js" {
			continue
		}
		if err := os.WriteFile(filepath.Join(directory, name), []byte("fixture"), 0600); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := loadSettings(); err == nil {
		t.Fatal("missing imported module accepted")
	}
	if err := os.WriteFile(filepath.Join(directory, "input.js"), []byte("fixture"), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err := loadSettings(); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(filepath.Join(directory, "input.js")); err != nil {
		t.Fatal(err)
	}
	if err := os.Mkdir(filepath.Join(directory, "input.js"), 0700); err != nil {
		t.Fatal(err)
	}
	if _, err := loadSettings(); err == nil {
		t.Fatal("directory accepted as a module")
	}
}
