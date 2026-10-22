"""Independent A deadline. Never starts product or injects input."""
import json, subprocess, sys, time
from manual_e2e_desktop import OUT, ROOT, close_sessions


def watch():
    state=json.loads((OUT/'run.json').read_text(encoding='utf-8'))
    (OUT/'watchdog-ready.json').write_text(json.dumps({'activeEnd':state['activeEnd']}),encoding='utf-8')
    while time.monotonic()<state['activeEnd']:
        if (OUT/'failed.json').exists() or (OUT/'stop').exists(): break
        time.sleep(.1)
    # Block subsequent commands before cleanup, including commands from a new terminal.
    (OUT/'failed.json').write_text('{"reason":"RunEndedCleanupRequired"}',encoding='utf-8')
    errors=[]
    try:
        subprocess.run([sys.executable,str(ROOT/'scripts/e2e-ssh-command.py'),
                        '--script',str(OUT/'stop-b.ps1'),'--output',str(OUT/'stop-b-result.json'),
                        '--timeout','15'],timeout=25,check=True,capture_output=True)
    except Exception as error: errors.append(type(error).__name__+': B stop unconfirmed')
    try: close_sessions()
    except Exception as error: errors.append(str(error))
    (OUT/'watchdog-result.json').write_text(json.dumps({
        'errors':errors,'finished':time.monotonic(),'desktopReturnVerified':False,
        'note':'Inspect B cleanup and both desktops; this is not a functional PASS.'}),encoding='utf-8')


if __name__=='__main__': watch()
