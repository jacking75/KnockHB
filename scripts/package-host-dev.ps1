[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][uri]$SignalingUri,
    [Parameter(Mandatory=$true)][string]$SdkDirectory,
    [Parameter(Mandatory=$true)][string]$Output,
    [string]$SignalingCertificate
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot=Join-Path $repo '.artifacts'
$dest=[IO.Path]::GetFullPath($Output)
if(!$dest.StartsWith($artifactRoot+'\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $dest)){throw '새 .artifacts 하위 출력 경로가 필요하다.'}
if($SignalingUri.Scheme-ne'https'-or$SignalingUri.UserInfo-or$SignalingUri.Query-or$SignalingUri.Fragment-or$SignalingUri.AbsolutePath-ne'/'){throw '검토한 HTTPS signaling origin이 필요하다.'}
$sdk=[IO.Path]::GetFullPath($SdkDirectory)
$msbuild=Join-Path $sdk 'MSBuild.dll'
if(!(Test-Path -LiteralPath $msbuild)-or(Split-Path $sdk -Leaf)-notmatch'^10\.0\.\d+$'){throw '.NET 10 SDK 디렉터리가 필요하다.'}
# This is an explicitly selected development SDK; global.json remains unchanged.
$originalSdkPath=$env:MSBuildSDKsPath
$env:MSBuildSDKsPath=Join-Path $sdk 'Sdks'
$utf8=New-Object Text.UTF8Encoding($false)
$payload=Join-Path $dest 'payload'
New-Item -ItemType Directory -Path $payload -Force|Out-Null
try{
    # RID-specific restore needs its own lock files. Preserve the working tree locks.
    $source=Join-Path $dest 'build-source'
    # Include new source files in a dirty candidate; git ls-files alone omits untracked implementations.
    $sourceFiles=@(& git -C $repo -c core.quotepath=false ls-files --cached --others --exclude-standard -- src deploy/runbooks deploy/prepare-host.ps1 deploy/prepare-sas.ps1 third_party/ffmpeg Directory.Build.props Directory.Build.targets Directory.Packages.props global.json NuGet.Config nuget.config | Sort-Object -Unique)
    if($LASTEXITCODE-ne0-or!$sourceFiles.Count){throw 'Source inventory failed'}
    foreach($relative in $sourceFiles){
        $sourcePath=[IO.Path]::GetFullPath((Join-Path $repo $relative))
        if(!$sourcePath.StartsWith($repo+'\',[StringComparison]::OrdinalIgnoreCase)-or(Get-Item -LiteralPath $sourcePath -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Source inventory path or reparse mismatch'}
        $target=Join-Path $source $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null
        Copy-Item -LiteralPath $sourcePath -Destination $target
    }
    $sourceHashes=@($sourceFiles|ForEach-Object {@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $source $_)).Hash}})
    foreach($name in @('Service','Tray','Helper','AdminHelper','RunbookWorker')){
        $project=Join-Path $source ('src/RemoteAssist.Host.'+$name+'/RemoteAssist.Host.'+$name+'.csproj')
        & dotnet $msbuild $project /restore /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishSingleFile=false /p:RestoreLockedMode=false (('/p:PublishDir='+$payload+'\')) /v:minimal /nologo
        if($LASTEXITCODE-ne0){throw ('개발 publish 실패: '+$name)}
    }
    # Ship the same elevated interactive launch that issues the one-use support grant.
    $updaterOutput=Join-Path $dest 'updater'
    & dotnet $msbuild (Join-Path $source 'src/RemoteAssist.Update/RemoteAssist.Update.csproj') /restore /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:RestoreLockedMode=false (('/p:PublishDir='+$updaterOutput+'\')) /v:minimal /nologo
    if($LASTEXITCODE-ne0){throw 'Development support launcher publish failed'}
    Copy-Item -LiteralPath (Join-Path $updaterOutput 'RemoteAssist.Update.exe') -Destination $payload
    $native=Join-Path $repo 'third_party/ffmpeg/bin'
    $dlls=@(Get-ChildItem -LiteralPath $native -Filter '*.dll' -File)
    if(!$dlls.Count){throw '검증할 FFmpeg DLL이 없다.'}
    New-Item -ItemType Directory -Path (Join-Path $payload 'ffmpeg')|Out-Null
    $dlls|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $payload 'ffmpeg')}
    Copy-Item -LiteralPath (Join-Path $repo 'third_party/ffmpeg/notices') -Destination $payload -Recurse
    Copy-Item -LiteralPath (Join-Path $repo 'deploy/runbooks') -Destination $payload -Recurse
    Copy-Item -LiteralPath (Join-Path $source 'deploy/prepare-host.ps1') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $source 'deploy/prepare-sas.ps1') -Destination $payload
    $hostSettings=@{SignalingUri=$SignalingUri.AbsoluteUri;AllowLoopbackHttp=$false}
    if($SignalingCertificate){$hostSettings.SignalingCertificateBase64=[Convert]::ToBase64String([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($SignalingCertificate)))}
    [IO.File]::WriteAllText((Join-Path $payload 'host.settings.json'),($hostSettings|ConvertTo-Json),$utf8)
    $files=@(Get-ChildItem -LiteralPath $payload -Recurse -File|ForEach-Object {
        if($_.Attributes-band[IO.FileAttributes]::ReparsePoint){throw '후보 reparse 경로 금지'}
        @{path=$_.FullName.Substring($payload.Length+1);size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
    [IO.File]::WriteAllText((Join-Path $dest 'candidate.json'),(@{kind='UNSIGNED_DEVELOPMENT_NOT_RELEASE';commit=(& git -C $repo rev-parse HEAD);dirty=!!(& git -C $repo status --porcelain);sdk=(Split-Path $sdk -Leaf);createdUtc=[DateTimeOffset]::UtcNow.ToString('o');sources=$sourceHashes;files=$files;state='BUILT_NOT_INSTALLED_NOT_E2E_PASS'}|ConvertTo-Json -Depth 6),$utf8)
    Write-Output ('개발 후보 생성: '+$dest)
}finally{$env:MSBuildSDKsPath=$originalSdkPath}
