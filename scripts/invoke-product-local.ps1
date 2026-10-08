[CmdletBinding()]
param([switch]$Admin,[string]$RunDir,[ValidateRange(30,900)][int]$Seconds=600)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot=Join-Path $repository '.artifacts'
if(!$Admin) {
    $RunDir=Join-Path $artifactRoot ('product-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $RunDir | Out-Null
    $publish=Join-Path $RunDir 'publish'
    foreach($name in @('Service','Tray','Helper')) {
        & dotnet publish (Join-Path $repository ('src/RemoteAssist.Host.'+$name+'/RemoteAssist.Host.'+$name+'.csproj')) --no-restore -c Release --self-contained false -o $publish
        if($LASTEXITCODE -ne 0){throw '제품 publish 실패'}
    }
    $native=Join-Path $publish 'third_party/ffmpeg/bin'
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $repository 'third_party/ffmpeg/bin') -File | Copy-Item -Destination $native
    @{SignalingUri='http://127.0.0.1:25001';AllowLoopbackHttp=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'host.settings.json') -Encoding utf8
    $manifest=@(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object {
        if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'publish reparse point 금지'}
        @{path=$_.FullName.Substring($publish.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    ConvertTo-Json -InputObject $manifest -Depth 4 | Set-Content -LiteralPath (Join-Path $RunDir 'manifest.json') -Encoding utf8
    Write-Output "제품 로컬 실증 증거: $RunDir"
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "{0}" -Admin -RunDir "{1}" -Seconds {2}' -f $PSCommandPath,$RunDir,$Seconds
    $child=Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -Wait -PassThru
    if($child.ExitCode -ne 0){throw '관리자 제품 실증 실패. installer-error.json을 확인한다'}
    Get-Content -LiteralPath (Join-Path $RunDir 'cleanup.json') -Encoding utf8
    exit 0
}
$RunDir=[IO.Path]::GetFullPath($RunDir)
if(!$RunDir.StartsWith($artifactRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw '저장소 artifact 경로가 필요하다'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '관리자 승인이 필요하다'}
$serviceName='RemoteAssistHost'
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw '기존 제품 서비스를 덮어쓰지 않는다'}
$stage=[IO.Path]::GetFullPath((Join-Path $env:ProgramFiles ('RemoteAssist-Local-'+[guid]::NewGuid().ToString('N'))))
if(!$stage.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles)+'\',[StringComparison]::OrdinalIgnoreCase)){throw '보호 경로 검사 실패'}
$installed=$false
try {
    New-Item -ItemType Directory -Path $stage | Out-Null
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $inherit=[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $propagation=[Security.AccessControl.PropagationFlags]::None
    foreach($sid in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl',$inherit,$propagation,'Allow')))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')),'ReadAndExecute',$inherit,$propagation,'Allow')))
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $stage -AclObject $acl
    $publish=Join-Path $RunDir 'publish'
    $manifest=Get-Content -LiteralPath (Join-Path $RunDir 'manifest.json') -Encoding utf8 -Raw | ConvertFrom-Json
    foreach($item in $manifest) {
        $source=[IO.Path]::GetFullPath((Join-Path $publish $item.path));$target=[IO.Path]::GetFullPath((Join-Path $stage $item.path))
        if(!$source.StartsWith($publish+'\',[StringComparison]::OrdinalIgnoreCase) -or !$target.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw '배치 경로 이탈'}
        $parent=Split-Path -Parent $target
        if(!(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent | Out-Null}
        Copy-Item -LiteralPath $source -Destination $target
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $item.sha256){throw '배치 hash 불일치'}
    }
    @{utc=[DateTimeOffset]::UtcNow.ToString('o');protectedDirectory=$stage;manifestCount=$manifest.Count} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'install.json') -Encoding utf8
    $exe=Join-Path $stage 'RemoteAssist.Host.Service.exe'
    New-Service -Name $serviceName -BinaryPathName ('"{0}"' -f $exe) -StartupType Manual | Out-Null
    $installed=$true
    Start-Service -Name $serviceName
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while((Get-Service -Name $serviceName).Status -ne 'Stopped' -and $clock.Elapsed.TotalSeconds -lt $Seconds -and !(Test-Path -LiteralPath (Join-Path $RunDir 'finish'))) {Start-Sleep -Milliseconds 200}
    @{utc=[DateTimeOffset]::UtcNow.ToString('o');seconds=$clock.Elapsed.TotalSeconds;serviceState=(Get-Service -Name $serviceName).Status.ToString()} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'result.json') -Encoding utf8
} catch {
    @{utc=[DateTimeOffset]::UtcNow.ToString('o');error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'installer-error.json') -Encoding utf8
    exit 1
} finally {
    if($installed){Stop-Service -Name $serviceName -ErrorAction SilentlyContinue;& sc.exe delete $serviceName | Out-Null}
    $status=Join-Path $stage 'service-status.ndjson'
    if(Test-Path -LiteralPath $status){Copy-Item -LiteralPath $status -Destination (Join-Path $RunDir 'service-status.ndjson')}
    if(Test-Path -LiteralPath $stage){Remove-Item -LiteralPath $stage -Recurse -Force}
    @{utc=[DateTimeOffset]::UtcNow.ToString('o');serviceAbsent=!(Get-Service -Name $serviceName -ErrorAction SilentlyContinue);stageAbsent=!(Test-Path -LiteralPath $stage)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'cleanup.json') -Encoding utf8
}
