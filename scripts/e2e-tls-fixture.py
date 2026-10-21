"""B02 standalone TLS listener. No product session or trust-store writes.
Only an explicitly authorized future fixture run may call --execute.
"""
import argparse
from pathlib import Path
import json
import os
import time
from e2e_case_runtime import require,read_json,new_json,safe_path,CaseError,ROOT

def main(argv=None):
    parser=argparse.ArgumentParser();parser.add_argument('--config',required=True);parser.add_argument('--run-id',required=True)
    parser.add_argument('--output',required=True);parser.add_argument('--execute',action='store_true');args=parser.parse_args(argv)
    try:
        require(args.execute,'separate-fixture-execution-required')
        import re
        require(re.fullmatch('[a-f0-9]{32}',args.run_id) is not None,'fixture-run-id')
        config=read_json(args.config);require(config['a']['address']=='192.168.0.61','tls-fixture-machine')
        output=safe_path(args.output,ROOT/'.artifacts');require(not output.exists(),'tls-fixture-replay');output.mkdir(parents=True)
        certificate=safe_path(config['local']['tlsCert'],exists=True);key=safe_path(config['local']['tlsKey'],exists=True)
        import socket,ssl,ctypes
        from ctypes import wintypes
        api=ctypes.WinDLL('kernel32',use_last_error=True)
        api.GetCurrentProcess.restype=wintypes.HANDLE
        api.GetProcessTimes.argtypes=[wintypes.HANDLE,*([ctypes.POINTER(wintypes.FILETIME)]*4)]
        times=[wintypes.FILETIME() for _ in range(4)]
        require(api.GetProcessTimes(api.GetCurrentProcess(),*[ctypes.byref(t) for t in times]),'tls-fixture-identity')
        creation=(times[0].dwHighDateTime<<32)+times[0].dwLowDateTime+504911232000000000
        import sys
        new_json(output/'owner.json',{'runId':args.run_id,'pid':os.getpid(),'start':creation,'image':str(Path(sys.executable).resolve())})
        context=ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER);context.minimum_version=ssl.TLSVersion.TLSv1_2
        context.load_cert_chain(certificate,key)
        started=time.perf_counter_ns();deadline=started+90_000_000_000;handshakes=0
        with socket.socket() as listener:
            listener.bind(('192.168.0.61',25001));listener.listen(3);listener.settimeout(.5)
            new_json(output/'ready.json',{'runId':args.run_id,'tick':started,'deadlineTick':deadline,'frequency':1000000000,'productStarted':False})
            while time.perf_counter_ns()<deadline and not (output/'finish.json').exists():
                try:connection,_=listener.accept()
                except socket.timeout:continue
                with connection:
                    remaining=(deadline-time.perf_counter_ns())/1e9
                    require(remaining>0,'tls-fixture-deadline');connection.settimeout(min(3,remaining))
                    with context.wrap_socket(connection,server_side=True):handshakes+=1
                if handshakes==3:break
        new_json(output/'closed.json',{'runId':args.run_id,'listenerClosed':True,'handshakes':handshakes,
                                     'startedTick':started,'tick':time.perf_counter_ns(),'frequency':1000000000,'productStarted':False})
        return 0 if handshakes==3 else 10
    except (CaseError,OSError,KeyError,ValueError) as error:
        print(json.dumps({'state':'BLOCKED','code':getattr(error,'code',type(error).__name__),'functionalActions':0}));return 10

if __name__=='__main__':raise SystemExit(main())
