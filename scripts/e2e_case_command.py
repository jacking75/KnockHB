"""Bounded stdout/stderr worker. Started ONLY under e2e-case-command.ps1's Job."""
import base64
import subprocess
import sys
import threading
from e2e_case_runtime import read_json, new_json, require, safe_path, LIMIT


def run(request):
    r=read_json(request)
    exe=str(safe_path(r['executable'],exists=True))
    require(isinstance(r['arguments'],list) and all(isinstance(x,str) and '\0' not in x for x in r['arguments']), 'command-arguments')
    seconds=r['timeoutSeconds']
    require(type(seconds) in (int,float) and 0<seconds<=180,'command-timeout')
    p=subprocess.Popen([exe,*r['arguments']],stdin=subprocess.DEVNULL,stdout=subprocess.PIPE,
                       stderr=subprocess.PIPE,creationflags=subprocess.CREATE_NO_WINDOW)
    buffers=[bytearray(),bytearray()]
    overflow=threading.Event()
    def read(stream,index):
        while True:
            chunk=stream.read(4096)
            if not chunk: break
            if len(buffers[index])+len(chunk)>LIMIT:
                overflow.set()
                p.kill()
                break
            buffers[index].extend(chunk)
    threads=[threading.Thread(target=read,args=(s,i),daemon=True) for i,s in enumerate((p.stdout,p.stderr))]
    for t in threads:t.start()
    try:
        code=p.wait(timeout=seconds)
        for t in threads:t.join(timeout=0.1)
        require(not overflow.is_set() and not any(t.is_alive() for t in threads),'command-output-bound')
        # Never publish raw stderr; it may contain credentials or pairing text.
        new_json(r['output'],{'exitCode':code,'stdout':base64.b64encode(buffers[0]).decode('ascii'),
                             'stderrBytes':len(buffers[1])})
        return 0
    finally:
        if p.poll() is None:p.kill()


if __name__=='__main__':
    try: raise SystemExit(run(sys.argv[1]))
    except Exception: raise SystemExit(20)
