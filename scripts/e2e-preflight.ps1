[CmdletBinding()]
param(
    [string]$HostIp,
    [string]$TargetHost='192.168.0.74',
    [string]$TargetUser='jacking75NoteBook',
    [ValidateRange(25001,25199)][int]$Port=25001
)
# A(지원자) 머신에서 실행한다. 두 PC 실연결 테스트를 시작할 수 있는 상태인지 읽기 전용으로 점검한다.
# 아무것도 설치·실행·변경하지 않는다. 화면을 점유하지 않는다. 결과만 출력한다.
$ErrorActionPreference='Continue'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ok=$true
function Check([string]$Name,[bool]$Pass,[string]$Detail=''){
    $mark=if($Pass){'OK  '}else{'FAIL';$script:ok=$false}
    Write-Host ("{0} {1}{2}" -f $mark,$Name,$(if($Detail){' · '+$Detail}else{''}))
}

# 1. A 도구
$dotnet=(& dotnet --version 2>$null)
Check '.NET SDK 10' ($dotnet -and $dotnet.StartsWith('10.')) $dotnet
$sdk=Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory -ErrorAction SilentlyContinue | Where-Object {$_.Name -match '^10\.\d+\.\d+$'} | Sort-Object Name -Descending | Select-Object -First 1
Check '.NET 10 SDK 디렉터리' ($null -ne $sdk) $(if($sdk){$sdk.FullName}else{'C:\Program Files\dotnet\sdk\10.* 없음'})
$go=(& go version 2>$null)
Check 'Go' ($go -match 'go1\.(2[6-9]|[3-9])') $go
$crypto=(& python -c "import cryptography,sys;sys.stdout.write(cryptography.__version__)" 2>$null)
Check 'Python cryptography (TLS 인증서 생성용)' ($LASTEXITCODE -eq 0 -and $crypto) $(if($crypto){"cryptography $crypto"}else{'pip install cryptography 필요'})

# 2. A 네트워크 주소
if(!$HostIp){
    $HostIp=(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object {$_.IPAddress -match '^(192\.168|10\.|172\.(1[6-9]|2\d|3[01]))\.' -and
            $_.InterfaceAlias -notmatch 'VMware|WSL|vEthernet|Loopback|Bluetooth|Tailscale'} |
        Select-Object -First 1 -ExpandProperty IPAddress)
}
Check 'A의 사설 LAN IPv4' ($null -ne $HostIp) $(if($HostIp){$HostIp}else{'LAN 주소를 못 찾음. -HostIp로 지정'})

# 3. 포트가 비어 있는지
$inUse=Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
Check "터널링 서버 포트 $Port 사용 가능" ($null -eq $inUse)

# 4. B 도달성 (ping + SSH + 같은 서브넷)
$ping=Test-Connection -ComputerName $TargetHost -Count 1 -Quiet -ErrorAction SilentlyContinue
Check "B($TargetHost) ping" $ping $(if(!$ping){'ICMP 차단일 수도 있음 — SSH 결과를 본다'})
$sshOut=(& ssh -o ConnectTimeout=5 -o BatchMode=yes "$TargetUser@$TargetHost" hostname 2>&1)
$sshOk=($LASTEXITCODE -eq 0)
Check "B SSH ($TargetUser@$TargetHost)" $sshOk $(if($sshOk){"hostname=$sshOut"}else{[string]$sshOut})
if($HostIp){
    $prefix=($HostIp -split '\.')[0..2] -join '.'
    Check "A와 B가 같은 /24 서브넷" ($TargetHost -like "$prefix.*") "A=$HostIp B=$TargetHost"
}

# 5. 브라우저
$chrome=(Get-Command chrome.exe -ErrorAction SilentlyContinue) -or (Test-Path 'C:\Program Files\Google\Chrome\Application\chrome.exe') -or (Test-Path 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe')
Check '브라우저(Chrome/Edge)' $chrome

Write-Host ''
if($ok){Write-Host "READY — e2e-prepare.ps1 로 준비를 진행할 수 있다."}
else{Write-Host "NOT READY — 위 FAIL 항목을 먼저 해결한다."}
exit $(if($ok){0}else{1})
