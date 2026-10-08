[CmdletBinding()]
param([switch]$Admin,[string]$RunDir,[switch]$TestSas,[ValidateRange(15,120)][int]$Seconds=120)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $repository '.artifacts'))
if(!$Admin) {
    $RunDir=Join-Path $artifactRoot ('boundary-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $RunDir | Out-Null
    $publish=Join-Path $RunDir 'publish'
    & dotnet publish (Join-Path $repository 'tools/RemoteAssist.BoundaryProbe/RemoteAssist.BoundaryProbe.csproj') --no-restore -c Release --self-contained false -o $publish
    if($LASTEXITCODE -ne 0){throw 'probe publish 실패'}
    $manifest=@(Get-ChildItem -LiteralPath $publish -Recurse -File | ForEach-Object {
        if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'publish reparse point 금지'}
        @{path=$_.FullName.Substring($publish.Length+1);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    ConvertTo-Json -InputObject $manifest -Depth 4 | Set-Content -LiteralPath (Join-Path $RunDir 'manifest.json') -Encoding utf8
    Write-Output "실증 증거: $RunDir"
    $arguments='-NoProfile -ExecutionPolicy Bypass -File "{0}" -Admin -RunDir "{1}"' -f $PSCommandPath,$RunDir
    if($TestSas){$arguments+=' -TestSas'}
    $arguments+=' -Seconds '+$Seconds
    $child=Start-Process -FilePath 'powershell.exe' -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -Wait -PassThru
    if($child.ExitCode -ne 0){throw "관리자 실증 실패: $($child.ExitCode)"}
    Get-Content -LiteralPath (Join-Path $RunDir 'result.ndjson') -Encoding utf8
    exit 0
}
$RunDir=[IO.Path]::GetFullPath($RunDir)
if(!$RunDir.StartsWith($artifactRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '저장소 artifact 경로가 필요하다'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw '관리자 승인이 필요하다'}
$serviceName='RemoteAssistBoundaryProbe'
if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){throw '기존 실증 서비스가 있다. 덮어쓰지 않는다'}
$stage=Join-Path $env:ProgramFiles ('RemoteAssist-Probe-'+[guid]::NewGuid().ToString('N'))
$expectedStage=[IO.Path]::GetFullPath($stage)
if(!$expectedStage.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw '보호 경로 검사 실패'}
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
        $source=[IO.Path]::GetFullPath((Join-Path $publish $item.path))
        $target=[IO.Path]::GetFullPath((Join-Path $stage $item.path))
        if(!$source.StartsWith($publish+'\',[StringComparison]::OrdinalIgnoreCase) -or !$target.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw '배치 경로 이탈'}
        $parent=Split-Path -Parent $target
        if(!(Test-Path -LiteralPath $parent)){New-Item -ItemType Directory -Path $parent | Out-Null}
        Copy-Item -LiteralPath $source -Destination $target
        if((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $item.sha256){throw '배치 hash 불일치'}
    }
    @{utc=[DateTimeOffset]::UtcNow.ToString('o');protectedDirectory=$stage;service=$serviceName;manifestCount=$manifest.Count} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'install.json') -Encoding utf8
    $exe=Join-Path $stage 'RemoteAssist.BoundaryProbe.exe'
    [IO.File]::WriteAllText((Join-Path $stage 'observation-seconds'),$Seconds.ToString())
    if($TestSas) {
        $policy=Get-ItemPropertyValue -LiteralPath HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/Policies/System -Name SoftwareSASGeneration -ErrorAction SilentlyContinue
        if($policy -ne 1 -and $policy -ne 3){throw '서비스 SAS 허용 정책이 없어 SAS 시험을 실행하지 않는다. 정책은 변경하지 않는다'}
        [IO.File]::WriteAllText((Join-Path $stage 'sas-request'),'local test')
    }
    New-Service -Name $serviceName -BinaryPathName ('"{0}" --service' -f $exe) -StartupType Manual | Out-Null
    $installed=$true
    Start-Service -Name $serviceName
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while((Get-Service -Name $serviceName).Status -ne 'Stopped' -and $clock.Elapsed.TotalSeconds -lt 150){Start-Sleep -Milliseconds 250}
    if((Get-Service -Name $serviceName).Status -ne 'Stopped'){throw 'probe 서비스 deadline 초과'}
    $report=Join-Path $stage 'result.ndjson'
    if(!(Test-Path -LiteralPath $report)){throw 'probe report 누락'}
    Copy-Item -LiteralPath $report -Destination (Join-Path $RunDir 'result.ndjson')
    if(Select-String -LiteralPath $report -Pattern 'service-error' -Quiet){throw '실증 서비스 오류. result.ndjson을 확인한다'}
} catch {
    @{utc=[DateTimeOffset]::UtcNow.ToString('o');error=$_.Exception.Message} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'installer-error.json') -Encoding utf8
    exit 1
} finally {
    if($installed) {
        Stop-Service -Name $serviceName -ErrorAction SilentlyContinue
        & sc.exe delete $serviceName | Out-Null
    }
    if([IO.Path]::GetFullPath($stage) -eq $expectedStage -and (Test-Path -LiteralPath $stage)) {Remove-Item -LiteralPath $stage -Recurse -Force}
    @{utc=[DateTimeOffset]::UtcNow.ToString('o');serviceAbsent=!(Get-Service -Name $serviceName -ErrorAction SilentlyContinue);stageAbsent=!(Test-Path -LiteralPath $stage)} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $RunDir 'cleanup.json') -Encoding utf8
}
