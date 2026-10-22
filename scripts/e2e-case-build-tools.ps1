[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Output)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'e2e-run-contract.ps1')
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$destination=[IO.Path]::GetFullPath($Output)
$allowed=Join-Path $repo '.artifacts'
if(!$destination.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $destination)){
 throw 'A new workspace .artifacts output is required'
}
$null=Assert-E2ePath (Split-Path $destination)
New-Item -ItemType Directory -Path $destination|Out-Null
$scratch=Join-Path $env:TEMP ('ra-case-build-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch|Out-Null
$import=[Security.SecurityElement]::Escape((Join-Path $repo 'Directory.Build.props'))
$intermediate=[Security.SecurityElement]::Escape((Join-Path $scratch 'obj'))
$properties=@'
<Project><Import Project="{0}"/><PropertyGroup>
<BaseIntermediateOutputPath>{1}/$(MSBuildProjectName)/</BaseIntermediateOutputPath>
<DefaultItemExcludes>$(DefaultItemExcludes);$(MSBuildProjectDirectory)/obj/**;$(MSBuildProjectDirectory)/bin/**</DefaultItemExcludes>
<NuGetAudit>false</NuGetAudit>
</PropertyGroup></Project>
'@ -f $import,$intermediate
$props=Join-Path $scratch 'build.props'
[IO.File]::WriteAllText($props,$properties,(New-Object Text.UTF8Encoding($false)))
foreach($entry in @(
 @{project='scripts/e2e-desktop/E2eDesktop.csproj';folder='desktop-tool'},
 @{project='tools/RemoteAssist.InputTarget/RemoteAssist.InputTarget.csproj';folder='input-target'},
 @{project='tests/e2e/scm-fixture/ScmFixture.csproj';folder='scm-fixture'})){
 $target=Join-Path $destination $entry.folder
 & dotnet build (Join-Path $repo $entry.project) -c Release --nologo ('-p:DirectoryBuildPropsPath='+$props) ('-p:OutputPath='+$target+'/')
 if($LASTEXITCODE-ne0){throw ('Tool build failed: '+$entry.folder)}
}
$files=@(foreach($entry in @(@{folder='desktop-tool';exe='E2eDesktop.exe'},@{folder='input-target';exe='RemoteAssist.InputTarget.exe'},@{folder='scm-fixture';exe='ScmFixture.exe'})){
 $target=Join-Path $destination $entry.folder
 if(!(Test-Path -LiteralPath (Join-Path $target $entry.exe))){throw 'Build output executable missing'}
 foreach($file in Get-ChildItem -LiteralPath $target -Recurse -File){
  $null=Assert-E2ePath $file.FullName
  @{path=$file.FullName.Substring($destination.Length+1);sha256=(Get-FileHash -LiteralPath $file.FullName).Hash;bytes=$file.Length}
 }
})
$sourceFiles=@((Join-Path $repo 'Directory.Build.props'),(Join-Path $repo 'Directory.Packages.props'))
$sourceFiles+=Join-Path $repo 'src/RemoteAssist.Host.Core/Signaling/SignalingTrust.cs'
$sourceFiles+=@((Join-Path $repo 'tests/e2e/ScmFixture.cs'),(Join-Path $repo 'tests/e2e/scm-fixture/ScmFixture.csproj'))
foreach($folder in @('scripts/e2e-desktop','tools/RemoteAssist.InputTarget')){
 $sourceFiles+=@(Get-ChildItem -LiteralPath (Join-Path $repo $folder) -File|Where-Object {$_.Extension-in@('.cs','.csproj')}|ForEach-Object {$_.FullName})
}
$sources=@($sourceFiles|ForEach-Object {@{path=$_;sha256=(Get-FileHash -LiteralPath $_).Hash}})
Write-E2eNewJson (Join-Path $destination 'build-result.json') @{state='BUILT_NOT_EXECUTED';desktopToolDirectory=(Join-Path $destination 'desktop-tool');inputTargetDirectory=(Join-Path $destination 'input-target');files=$files;sources=$sources}
