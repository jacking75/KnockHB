"""Execute a reviewed preparation script on the explicitly authorized, pinned test PC."""
import argparse,base64,json,os,socket,sys,time,winreg,re,uuid
from pathlib import Path
root=Path(__file__).resolve().parent.parent
sys.path.insert(0,str(root/'.artifacts/python-ssh'))
import paramiko
p=argparse.ArgumentParser();p.add_argument('--script',required=True);p.add_argument('--output',required=True);p.add_argument('--timeout',type=int,default=60)
p.add_argument('--staging')
p.add_argument('--campaign',help='Explicit local frozen campaign JSON; product actions reject legacy pointers')
p.add_argument('--remote-deadline',type=int)
p.add_argument('--remote-active-end',type=int)
p.add_argument('--upload',nargs=2,action='append',default=[],metavar=('LOCAL','REMOTE'))
p.add_argument('--download',nargs=2,action='append',default=[],metavar=('REMOTE','LOCAL'))
a=p.parse_args()
source=Path(a.script).resolve();output=Path(a.output).resolve()
if root not in source.parents or (root/'.artifacts').resolve() not in output.parents or output.exists() or not 1<=a.timeout<=180:
    raise SystemExit('Reviewed workspace script, new artifact output and bounded timeout required')
code=source.read_text(encoding='utf-8-sig')
campaign=None
if source.name.startswith('e2e-product-') and not a.campaign:
    raise SystemExit('Product action requires --campaign; legacy stage/run pointers are disabled')
if a.campaign:
    import hashlib
    campaign_path=Path(a.campaign).resolve()
    if (root/'.artifacts').resolve() not in campaign_path.parents or not a.staging:
        raise SystemExit('Workspace artifact campaign and explicit staging required')
    campaign=json.loads(campaign_path.read_text(encoding='utf-8-sig'))
    if not re.fullmatch('[a-f0-9]{32}',campaign['id']) or campaign['staging']!=a.staging:
        raise SystemExit('Frozen campaign staging mismatch')
    campaign_hash=hashlib.sha256(campaign_path.read_bytes()).hexdigest()
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
        # Reuse the pinned preparation connection for explicitly named evidence/package files.
        if a.upload or a.download:
            with paramiko.SFTPClient.from_transport(transport) as sftp:
                sftp.get_channel().settimeout(20)
                for local,remote in a.upload:
                    path=Path(local).resolve()
                    if root not in path.parents or not path.is_file():raise RuntimeError('Upload must be a workspace file')
                    sftp.put(str(path),remote,confirm=True)
                for remote,local in a.download:
                    path=Path(local).resolve()
                    if (root/'.artifacts').resolve() not in path.parents or path.exists():raise RuntimeError('New artifact download required')
                    with path.open('xb') as received:sftp.getfo(remote,received)
        channel=transport.open_session(timeout=5);channel.settimeout(5)
        prefix="$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new(); $ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue';\n"
        if a.staging:
            if not re.fullmatch(r'C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+',a.staging):raise RuntimeError('Unexpected staging path')
            prefix+="$Staging='"+a.staging+"';\n"
        if campaign:
            contract=next(t for t in campaign['tools'] if t['path']=='scripts/e2e-run-contract.ps1')
            if not re.fullmatch('[a-fA-F0-9]{64}',contract['sha256']):raise RuntimeError('Invalid contract hash')
            prefix+=("$CampaignManifestSha256='"+campaign_hash+"';\n"
                     "$manifestPath=Join-Path $Staging 'campaign.json';\n"
                     "if((Get-FileHash -LiteralPath $manifestPath).Hash-ne$CampaignManifestSha256){throw 'Campaign hash mismatch'};\n"
                     "$contractPath=Join-Path $Staging 'tools/scripts/e2e-run-contract.ps1';\n"
                     "if((Get-FileHash -LiteralPath $contractPath).Hash-ne'"+contract['sha256']+"'){throw 'Contract hash mismatch'};\n"
                     ". $contractPath; $CampaignManifest=Read-E2eManifest $manifestPath $CampaignManifestSha256;\n")
            if a.remote_deadline is not None or a.remote_active_end is not None:
                if not a.remote_deadline or not a.remote_active_end or not 0<a.remote_active_end<a.remote_deadline:raise RuntimeError('Invalid monotonic grant')
                prefix+='$RemoteDeadlineTick='+str(a.remote_deadline)+'L; $RemoteActiveEndTick='+str(a.remote_active_end)+'L;\n'
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
            if len(stdout)+len(stderr)>1048576:
                channel.close()
                output.write_text(json.dumps({'state':'COMPLETION_UNKNOWN_OUTPUT_BOUND','stdout':stdout[:16384].decode('utf-8-sig',errors='replace'),'stderr':stderr[:16384].decode('utf-8-sig',errors='replace')},ensure_ascii=False,indent=2),encoding='utf-8')
                raise RuntimeError('Remote output bound; partial diagnostic artifact saved, inspect before retry')
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
