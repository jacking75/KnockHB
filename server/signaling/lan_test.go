package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"math/big"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestLANRequiresTLSAndPrivateAddressOnly(t *testing.T) {
	for _, key := range []string{"RA_SESSION_TTL", "RA_ALLOWED_ORIGINS", "RA_OPERATOR_SECRET_FILE", "RA_AUDIT_DIR", "RA_VIEWER_DIR"} {
		t.Setenv(key, "")
	}
	dir := t.TempDir()
	write := func(name string, data []byte) string {
		p := filepath.Join(dir, name)
		if err := os.WriteFile(p, data, 0600); err != nil {
			t.Fatal(err)
		}
		return p
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	cert := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "LAN fixture"}, NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(time.Hour), IPAddresses: []net.IP{net.ParseIP("192.168.0.61")}, KeyUsage: x509.KeyUsageDigitalSignature, ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	der, err := x509.CreateCertificate(rand.Reader, cert, cert, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	priv, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("RA_MODE", "lan")
	t.Setenv("RA_LISTEN", "192.168.0.61:25001")
	t.Setenv("RA_TLS_CERT", write("cert.pem", pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})))
	t.Setenv("RA_TLS_KEY", write("key.pem", pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: priv})))
	// Operator credential, audit directory, origins and viewer directory are optional.
	if _, err := loadSettings(); err != nil {
		t.Fatal("minimal LAN configuration rejected", err)
	}
	for _, bad := range []struct{ key, value string }{{"RA_LISTEN", "8.8.8.8:25001"}, {"RA_TLS_CERT", ""}, {"RA_TLS_KEY", ""}, {"RA_OPERATOR_SECRET_FILE", filepath.Join(dir, "missing.secret")}} {
		t.Run(bad.key+bad.value, func(t *testing.T) {
			t.Setenv(bad.key, bad.value)
			if _, err := loadSettings(); err == nil {
				t.Fatal("unsafe/incomplete config accepted")
			}
		})
	}
}
