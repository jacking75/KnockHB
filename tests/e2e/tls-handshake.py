"""Input-free TLS preflight; uses no product session or global trust changes."""
import argparse
from pathlib import Path
import socket
import ssl
import threading

parser=argparse.ArgumentParser()
parser.add_argument('--directory',required=True)
args=parser.parse_args()
directory=Path(args.directory)
server=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
server.minimum_version=ssl.TLSVersion.TLSv1_2
server.load_cert_chain(directory/'server-cert.pem',directory/'server-key.pem')
client=ssl.create_default_context(cafile=str(directory/'server-cert.pem'))
errors=[]
with socket.socket() as listener:
    listener.settimeout(3)
    listener.bind(('127.0.0.1',25051))
    listener.listen(2)
    def serve():
        try:
            for _ in range(2):
                connection,_=listener.accept()
                with connection:
                    connection.settimeout(3)
                    try:
                        with server.wrap_socket(connection,server_side=True) as tls:
                            tls.sendall(b'OK')
                    except ssl.SSLError:
                        pass  # Wrong-name negative case aborts the server handshake.
        except Exception as error:
            errors.append(type(error).__name__)
    worker=threading.Thread(target=serve)
    worker.start()
    try:
        with socket.create_connection(('127.0.0.1',25051),timeout=3) as connection:
            with client.wrap_socket(connection,server_hostname='192.168.0.61') as tls:
                assert tls.recv(2)==b'OK'
        try:
            with socket.create_connection(('127.0.0.1',25051),timeout=3) as connection:
                with client.wrap_socket(connection,server_hostname='192.168.0.74'):
                    raise AssertionError('Wrong certificate name accepted')
        except ssl.SSLCertVerificationError:
            pass
    finally:
        worker.join(timeout=4)
    assert not worker.is_alive() and not errors,errors
print('PASS: TLS handshake, matching IP SAN, wrong-name rejection, owned listener closed; no product session')
