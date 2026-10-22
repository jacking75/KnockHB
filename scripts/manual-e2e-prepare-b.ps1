$ErrorActionPreference='Stop'
$base=Join-Path $env:USERPROFILE 'RemoteAssist-Test/20261022-2243'
New-Item -ItemType Directory -Path $base -Force | Out-Null
$source=Join-Path $env:USERPROFILE 'RemoteAssist-E2E-196568206174101705998361604504128602726/tools'
$package=Join-Path $source 'candidate'
if((Get-FileHash "$package/candidate.json").Hash -ne 'C0F16E2386F3835CC7BDD1A5F61C37459035A7912BA6214B7A2D3BA0F7C7CA4F'){throw 'Candidate mismatch'}
if(Get-Service RemoteAssistHost -ErrorAction SilentlyContinue){throw 'Service already exists'}
$data='C:\ProgramData\RemoteAssist'
$archive='C:\ProgramData\RemoteAssist-Preserved-20261022-2243'
if(Test-Path $data){
 $owner=Get-Content "$data/dev-owner.json" -Raw | ConvertFrom-Json
 if($owner.kind-ne'UNSIGNED_DEVELOPMENT_NOT_RELEASE'-or$owner.data-ne$data-or(Test-Path $archive)){throw 'Unknown existing state'}
 if((Resolve-Path $data).Path-ne$data){throw 'Invalid source'}
 if(@(Get-ChildItem $data -Recurse -Force | Where-Object {$_.Attributes-band[IO.FileAttributes]::ReparsePoint}).Count){throw 'Reparse refused'}
 Move-Item -LiteralPath $data -Destination $archive
}
Copy-Item -LiteralPath $package -Destination "$base/package" -Recurse -ErrorAction Stop
Add-Type -Path "$source/scripts/e2e-native.cs"
& "$source/scripts/install-host-dev.ps1" -Action Install -Package "$base/package" -OwnerId '78444836ee3944f6a03ebaf78144786c' -DeadlineTick ([E2eOwnedCommand]::Deadline(120000))
if(!(Get-Service RemoteAssistHost -ErrorAction SilentlyContinue)){throw 'Installation missing'}
Start-ScheduledTask -TaskName 'RemoteAssist-C01-DTD-20261022-2143-B'
@{installed=$true;started=$false;archive=$archive;package=$base;utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json
