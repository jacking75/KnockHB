[CmdletBinding()]
param([ValidateSet('Validate','Install','Remove')][string]$Action='Validate',[string]$Package)
# 개발 설치: package-host-dev.ps1가 만든 후보를 Program Files\RemoteAssist-Dev에 설치하고 서비스를 등록한다.
# 대상자가 RemoteAssist.Update.exe를 관리자 권한으로 한 번 실행하면 지원이 시작된다(support-dev).
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'RemoteAssist-Dev'
$service='RemoteAssistHost'
$registry='HKLM:\SYSTEM\CurrentControlSet\Services\RemoteAssistHost'
$image=Join-Path $root 'RemoteAssist.Host.Service.exe'

function Require-Admin {
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '관리자 권한 PowerShell에서 실행해야 한다.'}
}
function Run([string]$File,[string[]]$Arguments,[int[]]$Allowed=@(0)){
    # Start-Process joins the array without quoting; quote arguments that contain spaces.
    $quoted=@($Arguments|ForEach-Object {if($_-match'\s'-and$_-notmatch'^".*"$'){'"'+$_+'"'}else{$_}})
    $process=Start-Process -FilePath $File -ArgumentList $quoted -NoNewWindow -Wait -PassThru
    if($process.ExitCode-notin$Allowed){throw ("$File 실패: "+$process.ExitCode)}
}
function Prepare([string]$Mode){
    $script=Join-Path $root 'prepare-host.ps1'
    Run (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$script,'-Action',$Mode,'-InstallationRoot',$root)
}
function Stop-Host {
    if(!(Test-Path $registry)){return}
    if((Get-ItemProperty -LiteralPath $registry).ImagePath-ne('"'+$image+'"')){throw '다른 설치의 RemoteAssistHost 서비스다.'}
    Run (Join-Path $env:SystemRoot 'System32\sc.exe') @('stop',$service) @(0,1062)
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while(@(Get-Process -Name 'RemoteAssist.Host.*' -ErrorAction SilentlyContinue|Where-Object {$_.Path-and$_.Path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)}).Count-and[DateTime]::UtcNow-lt$deadline){Start-Sleep -Milliseconds 200}
}

if($Action-eq'Remove'){
    Require-Admin
    Stop-Host
    if(Test-Path $registry){Run (Join-Path $env:SystemRoot 'System32\sc.exe') @('delete',$service)}
    if(Test-Path (Join-Path $root 'prepare-host.ps1')){Prepare 'Remove'}
    if(Test-Path $root){Remove-Item -LiteralPath $root -Recurse -Force}
    # 제거하면 지원도 끝난다. 다음 설치 뒤 서비스가 옛 허가로 바로 대기하지 않게 허가를 지운다.
    $grant=Join-Path $env:ProgramData 'RemoteAssist\initial-support.json'
    if(Test-Path -LiteralPath $grant){Remove-Item -LiteralPath $grant -Force}
    Write-Output '개발 설치를 제거했다. 지원 허가를 지웠고 ProgramData의 지원 ID·감사 기록은 남긴다.'
    exit 0
}

if(!$Package){throw '-Package 후보 폴더가 필요하다.'}
$payload=Join-Path ([IO.Path]::GetFullPath($Package)) 'payload'
$candidate=Get-Content -LiteralPath (Join-Path $Package 'candidate.json') -Raw -Encoding UTF8|ConvertFrom-Json
foreach($file in $candidate.files){
    $source=Join-Path $payload $file.path
    if((Get-Item -LiteralPath $source).Length-ne$file.size-or(Get-FileHash -LiteralPath $source).Hash-ne$file.sha256){throw ('후보 파일이 바뀌었다: '+$file.path)}
}
if($Action-eq'Validate'){Write-Output ('후보 확인: '+$candidate.files.Count+'개 파일');exit 0}

Require-Admin
Stop-Host
if(Test-Path $root){Remove-Item -LiteralPath $root -Recurse -Force}
New-Item -ItemType Directory -Path $root|Out-Null
Copy-Item -Path (Join-Path $payload '*') -Destination $root -Recurse
if(!(Test-Path $registry)){Run (Join-Path $env:SystemRoot 'System32\sc.exe') @('create',$service,'binPath=',('"\"'+$image+'\""'),'start=','auto','obj=','LocalSystem')}
try{Prepare 'Prepare';Prepare 'Verify';Prepare 'Commit'}
catch{Prepare 'Rollback';throw}
Write-Output ('개발 설치 완료: '+$root+'. 대상 사용자가 RemoteAssist.Update.exe를 관리자 권한으로 실행하면 지원을 시작한다.')
