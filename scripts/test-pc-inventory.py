"""Read-only SSH inventory; credentials come from the named Windows user environment value."""
import argparse
import base64
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import socket
import sys
import time
import winreg

root=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(root/'.artifacts/python-ssh'))
import paramiko

parser=argparse.ArgumentParser()
parser.add_argument('--host',required=True)
parser.add_argument('--port',type=int,default=22)
parser.add_argument('--stage-file',help='Optional prepared ZIP under local .artifacts; upload only, never execute')
args=parser.parse_args()
address=ipaddress.ip_address(args.host)
if not address.is_private or address.is_loopback or address.is_unspecified:
    raise SystemExit('Explicit private test PC address required')
password=os.environ.get('jacking75NoteBookPW')
if not password:
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,'Environment') as environment:
        password=winreg.QueryValueEx(environment,'jacking75NoteBookPW')[0]
if not isinstance(password,str) or not password:
    raise SystemExit('Named credential is unavailable')
state=root/'.artifacts/ssh-state'
state.mkdir(exist_ok=True)
pinfile=state/(str(address).replace(':','_')+'-'+str(args.port)+'.json')
with socket.create_connection((str(address),args.port),timeout=4) as connection:
    transport=paramiko.Transport(connection)
    try:
        transport.start_client(timeout=5)
        key=transport.get_remote_server_key()
        fingerprint='SHA256:'+base64.b64encode(hashlib.sha256(key.asbytes()).digest()).decode().rstrip('=')
        observed={'host':str(address),'port':args.port,'keyType':key.get_name(),'key':key.get_base64(),'fingerprint':fingerprint}
        if pinfile.exists():
            if json.loads(pinfile.read_text(encoding='utf-8'))!=observed:
                raise RuntimeError('SSH host key changed; refusing authentication')
        else:
            # TOFU only for the explicitly supplied private test PC; later changes fail closed.
            with pinfile.open('x',encoding='utf-8') as output:json.dump(observed,output,indent=2)
        transport.auth_password('jacking75NoteBook',password,fallback=False)
        password=None
        command=r'''
$ErrorActionPreference='Stop'
$os=Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 5
$gpus=Get-CimInstance Win32_VideoController -OperationTimeoutSec 5
$service=Get-Service RemoteAssistHost -ErrorAction SilentlyContinue
$sessions=(& quser 2>&1 | Out-String)
@{computer=$env:COMPUTERNAME;profile=$env:USERPROFILE;admin=(New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);account=[Security.Principal.WindowsIdentity]::GetCurrent().Name;os=$os.Caption;build=$os.BuildNumber;gpu=@($gpus|Select-Object Name,DriverVersion);sessionText=$sessions;productService=if($service){$service.Status.ToString()}else{'absent'};productDataExists=(Test-Path "$env:ProgramData\RemoteAssist");developmentInstallExists=(Test-Path "$env:ProgramFiles\RemoteAssist-Dev");utc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 5
'''
        encoded=base64.b64encode(command.encode('utf-16-le')).decode()
        channel=transport.open_session(timeout=5)
        channel.exec_command('powershell.exe -NoProfile -NonInteractive -EncodedCommand '+encoded)
        deadline=time.monotonic()+20
        stdout=bytearray();stderr=bytearray()
        while time.monotonic()<deadline:
            if channel.recv_ready():stdout.extend(channel.recv(8192))
            if channel.recv_stderr_ready():stderr.extend(channel.recv_stderr(8192))
            if len(stdout)+len(stderr)>131072:raise RuntimeError('Inventory output limit')
            if channel.exit_status_ready() and not channel.recv_ready() and not channel.recv_stderr_ready():break
            time.sleep(.02)
        else:raise TimeoutError('Read-only inventory exceeded 20 seconds')
        if channel.recv_exit_status()!=0:raise RuntimeError('Read-only inventory failed')
        result=json.loads(stdout.decode('utf-8-sig',errors='replace'))
        result['sshFingerprint']=fingerprint
        destination=state/('inventory-'+str(time.time_ns())+'.json')
        destination.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        if args.stage_file:
            source=Path(args.stage_file).resolve()
            if (root/'.artifacts').resolve() not in source.parents or source.suffix!='.zip' or not source.is_file():
                raise RuntimeError('Only a prepared local artifact ZIP may be staged')
            profile=result['profile'].replace('\\','/')
            if not profile.lower().startswith('c:/users/') or '..' in profile:
                raise RuntimeError('Unexpected test profile path')
            remote=profile+'/RemoteAssist-E2E-'+str(time.time_ns())
            with paramiko.SFTPClient.from_transport(transport) as sftp:
                sftp.get_channel().settimeout(20)
                sftp.mkdir(remote)
                staged=remote+'/prepared.zip'
                sftp.put(str(source),staged,confirm=True)
                if sftp.stat(staged).st_size!=source.stat().st_size:raise RuntimeError('Staged length mismatch')
            result['stagedZip']=staged
            destination.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
        print(json.dumps({'status':'INVENTORY_ONLY_NOT_E2E_PASS','artifact':str(destination),'hostFingerprint':fingerprint}))
    finally:
        password=None
        transport.close()
