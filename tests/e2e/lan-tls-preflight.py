"""Bounded, input-free TLS connectivity probe; no product or trust-store changes."""
import http.server,ssl,threading,subprocess,sys,json,time
from pathlib import Path
root=Path(__file__).resolve().parents[2]
class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200 if self.path=='/healthz' else 404)
        self.end_headers()
    def log_message(self,*args):pass
server=http.server.HTTPServer(('192.168.0.61',25001),Handler)
context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
context.minimum_version=ssl.TLSVersion.TLSv1_2
context.load_cert_chain(root/'.artifacts/lan-tls-20261018/server-cert.pem',root/'.artifacts/lan-tls-20261018/server-key.pem')
server.socket=context.wrap_socket(server.socket,server_side=True)
server.timeout=.5
stop=threading.Event()
def serve():
    end=time.monotonic()+20
    while not stop.is_set() and time.monotonic()<end:server.handle_request()
thread=threading.Thread(target=serve,daemon=True);thread.start()
try:
    result=subprocess.run([sys.executable,str(root/'scripts/e2e-ssh-command.py'),'--script',str(root/'scripts/e2e-remote-tls-probe.ps1'),'--output',str(root/'.artifacts/lan-tls-remote-20261018.json'),'--timeout','15'],timeout=20)
finally:
    stop.set();server.server_close();thread.join(2)
print(json.dumps({'probeExitCode':result.returncode,'listenerClosed':True,'productStarted':False,'osTrustChanged':False}))
sys.exit(result.returncode)
