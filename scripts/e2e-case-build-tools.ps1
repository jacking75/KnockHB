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
 @{project='tools/RemoteAssist.InputTarget/RemoteAssist.InputTarget.csproj';folder='input-target'})){
 $target=Join-Path $destination $entry.folder
 & dotnet build (Join-Path $repo $entry.project) -c Release --nologo ('-p:DirectoryBuildPropsPath='+$props) ('-p:OutputPath='+$target+'/')
 if($LASTEXITCODE-ne0){throw ('Tool build failed: '+$entry.folder)}
}
Write-E2eNewJson (Join-Path $destination 'build-result.json') @{state='BUILT_NOT_EXECUTED';desktopToolDirectory=(Join-Path $destination 'desktop-tool');inputTargetDirectory=(Join-Path $destination 'input-target')}
