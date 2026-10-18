"""Generate a short-lived LAN TLS identity. Does not install trust or start a server."""
import argparse
import datetime as dt
import hashlib
import ipaddress
import json
from pathlib import Path
from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID

parser = argparse.ArgumentParser()
parser.add_argument('--directory', required=True)
parser.add_argument('--ip', required=True)
args = parser.parse_args()
address = ipaddress.ip_address(args.ip)
if not address.is_private or address.is_loopback or address.is_unspecified:
    raise SystemExit('A concrete private LAN address is required')
directory = Path(args.directory).resolve()
root = (Path(__file__).resolve().parent.parent / '.artifacts').resolve()
if root not in directory.parents or not directory.is_dir() or any(directory.iterdir()):
    raise SystemExit('An empty, ACL-protected directory under .artifacts is required')
now = dt.datetime.now(dt.timezone.utc)
key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, 'RemoteAssist LAN test ' + str(address))])
cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
        .public_key(key.public_key()).serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(minutes=5)).not_valid_after(now + dt.timedelta(days=7))
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.SubjectAlternativeName([x509.IPAddress(address)]), critical=False)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .add_extension(x509.KeyUsage(digital_signature=True, key_encipherment=True, content_commitment=False,
                       data_encipherment=False, key_agreement=False, key_cert_sign=False, crl_sign=False,
                       encipher_only=False, decipher_only=False), critical=True)
        .sign(key, hashes.SHA256()))
files = {'server-key.pem': key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8,
                                           serialization.NoEncryption()),
         'server-cert.pem': cert.public_bytes(serialization.Encoding.PEM),
         'lan-test.cer': cert.public_bytes(serialization.Encoding.DER)}
for name, data in files.items():
    with (directory / name).open('xb') as output:
        output.write(data)
metadata = {'ip':str(address), 'expiresUtc':cert.not_valid_after_utc.isoformat(),
            'sha256':hashlib.sha256(files['lan-test.cer']).hexdigest(),
            'thumbprint':cert.fingerprint(hashes.SHA1()).hex().upper(),
            'purpose':'LAN TLS only, not code signing', 'trustInstalled':False}
(directory / 'tls-metadata.json').write_text(json.dumps(metadata, indent=2),encoding='utf-8')
print(json.dumps(metadata))
