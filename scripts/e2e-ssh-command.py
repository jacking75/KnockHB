"""Execute a reviewed preparation script on the explicitly authorized, pinned test PC."""
import argparse,base64,json,os,socket,sys,time,winreg,re,uuid
from pathlib import Path
root=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(root/'.artifacts/python-ssh'))
import paramiko
p=argparse.ArgumentParser();p.add_argument('--script',required=True);p.add_argument('--output',required=True);p.add_argument('--timeout',type=int,default=60)
p.add_argument('--staging')
a=p.parse_args()
source=Path(a.script).resolve();output=Path(a.output).resolve()
if root not in source.parents or (root/'.artifacts').resolve() not in output.parents or output.exists() or not 1<=a.timeout<=180:
    raise SystemExit('Reviewed workspace script, new artifact output and bounded timeout required')
code=source.read_text(encoding='utf-8-sig')
if len(code)>65536:raise SystemExit('Script size bound')
pin=json.loads((root/'.artifacts/ssh-state/192.168.0.74-22.json').read_text(encoding='utf-8'))
password=os.environ.get('jacking75NoteBookPW')
if not password:
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,'Environment') as env:password=winreg.QueryValueEx(env,'jacking75NoteBookPW')[0]
with socket.create_connection(('192.168.0.74',22),timeout=4) as connection:
    transport=paramiko.Transport(connection)
    try:
        transport.start_client(timeout=5)
        key=transport.get_remote_server_key()
        if key.get_name()!=pin['keyType'] or key.get_base64()!=pin['key']:raise RuntimeError('SSH host key changed')
        transport.auth_password('jacking75NoteBook',password,fallback=False);password=None
        channel=transport.open_session(timeout=5);channel.settimeout(5)
        prefix="$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue';\n"
        if a.staging:
            if not re.fullmatch(r'C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+',a.staging):raise RuntimeError('Unexpected staging path')
            prefix+="$Staging='"+a.staging+"';\n"
        remote_script=None
        if a.staging:
            remote_script=a.staging+'/invoke-'+uuid.uuid4().hex+'.ps1'
            with paramiko.SFTPClient.from_transport(transport) as sftp:
                sftp.get_channel().settimeout(5)
                with sftp.open(remote_script,'wx') as uploaded:
                    uploaded.write((prefix+code).encode('utf-8-sig'))
            command='powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+remote_script+'"'
        else:
            command='powershell.exe -NoProfile -NonInteractive -EncodedCommand '+base64.b64encode((prefix+code).encode('utf-16-le')).decode()
            if len(command)>7500:raise RuntimeError('Use staging for scripts beyond the Windows SSH command-line limit')
        channel.exec_command(command);end=time.monotonic()+a.timeout
        stdout=bytearray();stderr=bytearray()
        while time.monotonic()<end:
            if channel.recv_ready():stdout.extend(channel.recv(8192))
            if channel.recv_stderr_ready():stderr.extend(channel.recv_stderr(8192))
            if len(stdout)+len(stderr)>1048576:raise RuntimeError('Remote output bound')
            if channel.exit_status_ready() and not channel.recv_ready() and not channel.recv_stderr_ready():break
            time.sleep(.02)
        else:
            channel.close()
            raise TimeoutError('Remote observation timeout; remote completion is unknown, inspect before retry')
        result={'exitCode':channel.recv_exit_status(),'stdout':stdout.decode('utf-8-sig',errors='replace'),'stderr':stderr.decode('utf-8-sig',errors='replace')}
        output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        if remote_script:
            with paramiko.SFTPClient.from_transport(transport) as sftp:
                sftp.get_channel().settimeout(5)
                sftp.remove(remote_script)
        print(json.dumps({'exitCode':result['exitCode'],'artifact':str(output)}))
        sys.exit(0 if result['exitCode']==0 else 1)
    finally:
        password=None;transport.close()
