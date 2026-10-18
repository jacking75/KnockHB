$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramData 'RemoteAssist-E2E-Product-4ceb58484cec4936865317c7901b7570'
$source=Join-Path $env:ProgramData 'RemoteAssist'
$destination=Join-Path $root 'preserved-state'
if([IO.Path]::GetFullPath($source)-ne'C:\ProgramData\RemoteAssist'-or[IO.Path]::GetFullPath($destination)-ne'C:\ProgramData\RemoteAssist-E2E-Product-4ceb58484cec4936865317c7901b7570\preserved-state'){throw 'Fixed state archive scope mismatch'}
if((Get-Service RemoteAssistHost -ErrorAction SilentlyContinue)-or(Test-Path $destination)){throw 'Service present or archive already exists'}
$owner=Get-Content (Join-Path $source 'dev-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.id-ne'4ceb58484cec4936865317c7901b7570'-or$owner.kind-ne'UNSIGNED_DEVELOPMENT_NOT_RELEASE'){throw 'State owner mismatch'}
foreach($entry in @((Get-Item $source),(Get-Item $root))+(Get-ChildItem $source -Recurse -Force)){
 if($entry.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse state refused'}
}
if(@(Get-Process -Name 'RemoteAssist.Host.*' -ErrorAction SilentlyContinue).Count){throw 'Host process remains'}
Move-Item -LiteralPath $source -Destination $destination
@{state='AUDIT_PRESERVED';sourceAbsent=!(Test-Path $source);destination=$destination}|ConvertTo-Json
