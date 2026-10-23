[CmdletBinding()]
param(
    [string]$HostIp,
    [string]$TargetHost='192.168.0.74',
    [string]$TargetUser='jacking75NoteBook',
    [string]$PasswordEnv,
    [string]$TargetDir='C:/ra-candidate',
    [ValidateRange(25001,25199)][int]$Port=25001,
    [switch]$Install,
    [switch]$StartTunnel
)
# A(지원자) 머신에서 실행한다. 화면을 점유하지 않는 준비를 자동화한다. Windows PowerShell 5.1에서 돈다.
#   1) A의 LAN IP용 자체 서명 TLS 인증서 생성
#   2) Host 후보 빌드(package-host-dev)
#   3) (-Install) 후보(candidate.json·payload)와 install-host-dev.ps1을 B로 복사하고 B에서 Validate+Install (SSH)
#   4) (-StartTunnel) 터널링 서버를 고정 경로에 빌드해 새 창에서 기동(go run의 임시 exe는 방화벽 확인 창을 매번 띄운다)
# B SSH가 비밀번호 인증이면 -PasswordEnv에 비밀번호를 담은 환경 변수 이름을 넘긴다.
# 그 다음은 B에서 RemoteAssist.Update.exe 관리자 실행 + UAC 승인. 이후는 브라우저 Viewer에서 진행한다.
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if(!$HostIp){
    $HostIp=(Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object {$_.IPAddress -match '^(192\.168|10\.|172\.(1[6-9]|2\d|3[01]))\.' -and
            $_.InterfaceAlias -notmatch 'VMware|WSL|vEthernet|Loopback|Bluetooth|Tailscale'} |
        Select-Object -First 1 -ExpandProperty IPAddress)
}
if(!$HostIp){throw 'A의 LAN IPv4를 찾지 못했다. -HostIp로 지정한다.'}
$sdk=(Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory | Where-Object {$_.Name -match '^10\.\d+\.\d+$'} | Sort-Object Name -Descending | Select-Object -First 1).FullName
if(!$sdk){throw '.NET 10 SDK 디렉터리를 찾지 못했다.'}
$run=Join-Path $repo ('.artifacts/e2e-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run|Out-Null
Write-Host "A IP=$HostIp  SDK=$sdk  작업=$run"

# 1) TLS 인증서 (prepare-e2e-tls.py는 미리 만든 빈 디렉터리를 요구한다)
$tls=Join-Path $run 'tls'
New-Item -ItemType Directory -Path $tls|Out-Null
& python (Join-Path $repo 'scripts/prepare-e2e-tls.py') --directory $tls --ip $HostIp | Out-Null
if($LASTEXITCODE -ne 0){throw 'TLS 인증서 생성 실패 (python cryptography 필요)'}
Write-Host "TLS 인증서: $tls (server-cert.pem / server-key.pem / lan-test.cer)"

# 2) Host 후보
$candidate=Join-Path $run 'host-candidate'
& $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $repo 'scripts/package-host-dev.ps1') -TunnelUri ("https://{0}:{1}/" -f $HostIp,$Port) -TunnelCertificate (Join-Path $tls 'lan-test.cer') -SdkDirectory $sdk -Output $candidate
if($LASTEXITCODE -ne 0){throw 'Host 후보 빌드 실패'}
Write-Host "Host 후보: $candidate"

# 3) B로 복사 + 설치
if($Install){
    . (Join-Path $PSScriptRoot 'e2e-ssh.ps1')
    $ssh=Initialize-E2ESsh $PasswordEnv
    $remote="$TargetUser@$TargetHost"
    $installer="powershell -NoProfile -ExecutionPolicy Bypass -File {0}/install-host-dev.ps1 -Action {1} -Package {0}"
    Write-Host "B($remote)로 복사 중..."
    & ssh @ssh $remote "powershell -NoProfile -Command ""if(Test-Path '$TargetDir'){Remove-Item -LiteralPath '$TargetDir' -Recurse -Force}; New-Item -ItemType Directory -Path '$TargetDir' | Out-Null"""
    if($LASTEXITCODE -ne 0){throw 'B 디렉터리 준비 실패 (SSH 확인)'}
    & scp -q @ssh -r (Join-Path $candidate 'candidate.json') (Join-Path $candidate 'payload') (Join-Path $repo 'scripts/install-host-dev.ps1') ("{0}:{1}/" -f $remote,$TargetDir)
    if($LASTEXITCODE -ne 0){throw 'B 복사 실패'}
    Write-Host 'B에서 후보 검증...'
    & ssh @ssh $remote ($installer -f $TargetDir,'Validate')
    if($LASTEXITCODE -ne 0){throw 'B 후보 검증 실패'}
    Write-Host 'B에 설치 (관리자 권한 SSH 세션이어야 한다)...'
    & ssh @ssh $remote ($installer -f $TargetDir,'Install')
    if($LASTEXITCODE -ne 0){throw 'B 설치 실패 (관리자 권한 SSH 필요. 예전 Helper Block 방화벽 규칙이 있으면 지운다)'}
    Write-Host 'B 설치 완료.'
}

# 4) 터널링 서버 기동
if($StartTunnel){
    $server=Join-Path $run 'tunnel.exe'
    Push-Location (Join-Path $repo 'server/tunnel');try{& go build -o $server .}finally{Pop-Location}
    if($LASTEXITCODE -ne 0){throw '터널링 서버 빌드 실패'}
    $start="`$env:RA_MODE='lan';`$env:RA_LISTEN='0.0.0.0:$Port';`$env:RA_TLS_CERT='$tls\server-cert.pem';`$env:RA_TLS_KEY='$tls\server-key.pem';`$env:RA_VIEWER_DIR='$repo\web\viewer';& '$server'"
    Start-Process $powershell -ArgumentList '-NoExit','-NoProfile','-Command',$start
    Write-Host "터널링 서버 기동: https://$HostIp`:$Port/  (/healthz 로 확인)"
}

Write-Host ''
Write-Host '다음(사람):'
Write-Host "  1) B에서 RemoteAssist.Update.exe 관리자 실행 + UAC 승인 → Tray에 지원 ID 표시"
Write-Host "  2) A 브라우저에서 https://$HostIp`:$Port/ 열고 지원 ID 입력"
Write-Host "  (인증서는 자체 서명 — 브라우저에서 예외 허용)"
Write-Host "결과 JSON/로그: $run"
