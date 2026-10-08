[CmdletBinding()]
param([Parameter(Mandatory)][string]$SecretFile,[Parameter(Mandatory)][string]$Realm,
 [Parameter(Mandatory)][string]$ExternalIp,[Parameter(Mandatory)][string[]]$AllowedPeerIp,
 [Parameter(Mandatory)][string]$Certificate,[Parameter(Mandatory)][string]$PrivateKey,
 [string]$Destination=(Join-Path $PSScriptRoot '../deploy/coturn/private'))
$ErrorActionPreference='Stop'
if($Realm -notmatch '^[a-zA-Z0-9.-]{1,128}$'){throw 'realm 형식 오류다.'}
$ip=$null;if(![Net.IPAddress]::TryParse($ExternalIp,[ref]$ip)-or $ip.AddressFamily -ne 'InterNetwork'){throw '공개 IPv4 주소가 필요하다.'}
if($AllowedPeerIp.Count -lt 1 -or $AllowedPeerIp.Count -gt 64){throw '허용 피어 IP 목록은 1~64개다.'}
foreach($peerAddress in $AllowedPeerIp){$parsed=$null;if(![Net.IPAddress]::TryParse($peerAddress,[ref]$parsed)){throw '피어 IP 형식 오류다.'}}
$secret=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $SecretFile)).Trim()
if($secret -notmatch '^[A-Za-z0-9_-]{32,128}$'){throw 'TURN secret은 32~128자 base64url 값이어야 한다.'}
if(Test-Path -LiteralPath $Destination){throw '기존 TURN staging을 덮어쓰지 않는다. 새 경로를 지정한다.'}
$null=New-Item -ItemType Directory -Path (Join-Path $Destination 'tls') -Force
if($IsWindows -or $env:OS -eq 'Windows_NT'){
 $acl=New-Object Security.AccessControl.DirectorySecurity;$acl.SetAccessRuleProtection($true,$false)
 foreach($sidText in @('S-1-5-18','S-1-5-32-544')){$sid=New-Object Security.Principal.SecurityIdentifier($sidText);$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
 Set-Acl -LiteralPath $Destination -AclObject $acl
}
Copy-Item -LiteralPath $Certificate -Destination (Join-Path $Destination 'tls/cert.pem')
Copy-Item -LiteralPath $PrivateKey -Destination (Join-Path $Destination 'tls/key.pem')
$lines=@('listening-port=25002','tls-listening-port=25003','min-port=27100','max-port=27199',"realm=$Realm","external-ip=$ExternalIp",'fingerprint','use-auth-secret',"static-auth-secret=$secret",'no-cli','no-multicast-peers','no-tlsv1','no-tlsv1_1','no-dtls','no-udp','cert=/etc/coturn/tls/cert.pem','pkey=/etc/coturn/tls/key.pem','user-quota=4','total-quota=200','max-bps=1250000','stale-nonce=600','max-allocate-lifetime=600','denied-peer-ip=0.0.0.0-255.255.255.255','denied-peer-ip=::-ffff:ffff:ffff:ffff:ffff:ffff:ffff:ffff','log-file=stdout','no-stdout-log')
$lines+=@($AllowedPeerIp|ForEach-Object {"allowed-peer-ip=$_"})
[IO.File]::WriteAllLines((Join-Path $Destination 'turnserver.conf'),$lines,(New-Object Text.UTF8Encoding($false)))
Write-Output '보호된 TURN 구성 생성 완료다. Linux 서버에서 파일 소유/권한·인증서·공개 IP·허용 목적지를 사전 확인한다.'
