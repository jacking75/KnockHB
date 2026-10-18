[CmdletBinding()]
param([ValidateSet('Validate','Install','Remove')][string]$Action='Validate',[Parameter(Mandatory=$true)][string]$Package,[long]$DeadlineTick=0,[string]$OwnerId)
$ErrorActionPreference='Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$utf8=New-Object Text.UTF8Encoding($false)
$root=Join-Path $env:ProgramFiles 'RemoteAssist-Dev'
$data=Join-Path $env:ProgramData 'RemoteAssist'
$service='RemoteAssistHost'
$registry='HKLM:\SYSTEM\CurrentControlSet\Services\RemoteAssistHost'
$marker=Join-Path $root 'dev-owner.json'
$packageRoot=[IO.Path]::GetFullPath($Package)
function Check-Time {if([E2eOwnedCommand]::Remaining($DeadlineTick)-eq0){throw 'Development operation deadline expired'}}
function No-Link([string]$Path){
    for($item=Get-Item -LiteralPath $Path -Force;$item;$item=$item.Parent){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse path refused'}}
    if(Test-Path -LiteralPath $Path -PathType Container){foreach($item in Get-ChildItem -LiteralPath $Path -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse tree refused'}}}
}
function Native([string]$Name,[string[]]$Values){
    Check-Time
    $command=[E2eOwnedCommand]::Start((Join-Path $env:SystemRoot ('System32\'+$Name)),$Values)
    try{$exit=$command.Wait($DeadlineTick,10000);if($exit-ne0){throw ('Native failure '+$Name+': '+$exit)}}finally{$command.Dispose()}
}
function Protect([string]$Path,[bool]$ReadUsers){
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
    if($ReadUsers){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))}
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Relative-File([string]$Base,[string]$Relative){
    if($Relative-notmatch'^[A-Za-z0-9_./\\-]+$'-or($Relative-split'[/\\]'|Where-Object {$_-in@('','..','.')})){throw 'Invalid manifest relative path'}
    $path=[IO.Path]::GetFullPath((Join-Path $Base $Relative))
    if(!$path.StartsWith($Base+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Manifest path escaped root'}
    return $path
}
if($Action-eq'Validate'){$DeadlineTick=[E2eOwnedCommand]::Deadline(180000)}
elseif($DeadlineTick-le0-or[E2eOwnedCommand]::Remaining($DeadlineTick)-gt180000){throw 'Existing global deadline with at most 180 seconds remaining for this operation required'}
Check-Time
if($Action-ne'Validate'){
    $parsedOwner=[guid]::Empty
    if(![guid]::TryParseExact($OwnerId,'N',[ref]$parsedOwner)){throw 'Exact campaign owner ID required'}
    $admin=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if(!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator installation required'}
}
if($Action-eq'Remove'){
    # Removal only applies to the exact protected development installation and owner marker.
    if(!(Test-Path $root)-and!(Test-Path $registry)){Write-Output 'NO_OWNED_INSTALLATION';exit 0}
    No-Link $root;No-Link $data
    $owner=Get-Content -LiteralPath $marker -Raw -Encoding UTF8|ConvertFrom-Json
    if($owner.id-ne$OwnerId-or$owner.kind-ne'UNSIGNED_DEVELOPMENT_NOT_RELEASE'-or$owner.root-ne$root-or$owner.data-ne$data){throw 'Development owner mismatch'}
    $dataOwner=Get-Content -LiteralPath (Join-Path $data 'dev-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if($dataOwner.id-ne$owner.id){throw 'State directory owner mismatch'}
    $image=Join-Path $root 'RemoteAssist.Host.Service.exe'
    if(Test-Path $registry){
        if((Get-ItemProperty -LiteralPath $registry).ImagePath-ne('"'+$image+'"')){throw 'Service belongs to another installation'}
        # Stop is a separate bounded command; tolerate ERROR_SERVICE_NOT_ACTIVE only.
        $stop=[E2eOwnedCommand]::Start((Join-Path $env:SystemRoot 'System32\sc.exe'),@('stop',$service))
        try{$exit=$stop.Wait($DeadlineTick,10000);if($exit-notin@(0,1062)){throw ('Service stop failed: '+$exit)}}finally{$stop.Dispose()}
    }
    $end=[Math]::Min($DeadlineTick,[E2eOwnedCommand]::Deadline(10000))
    do{
        $children=@(Get-Process -Name 'RemoteAssist.Host.*' -ErrorAction SilentlyContinue|Where-Object {$_.Path-and$_.Path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)})
        if(!$children.Count){break};Start-Sleep -Milliseconds 50
    }while([E2eOwnedCommand]::Remaining($end)-gt0)
    if($children.Count){throw 'Owned processes remain; preserve files and use the reviewed recovery procedure'}
    if(Test-Path $registry){Native 'sc.exe' @('delete',$service)}
    if(Test-Path $registry){throw 'Service deletion pending; preserve installation'}
    # Explicit fixed roots, ownership and reparse checks immediately before recursive removal.
    Check-Time;No-Link $root;No-Link $data
    if([IO.Path]::GetFullPath($root)-ne(Join-Path $env:ProgramFiles 'RemoteAssist-Dev')){throw 'Unsafe removal root'}
    Remove-Item -LiteralPath $root -Recurse -Force
    # Preserve audit/state data as product removal does; never delete user transfer files.
    Write-Output 'Development service/files removed; ProgramData audit/state preserved.'
    exit 0
}
No-Link $packageRoot
$manifestPath=Join-Path $packageRoot 'candidate.json'
$candidate=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8|ConvertFrom-Json
if($candidate.kind-ne'UNSIGNED_DEVELOPMENT_NOT_RELEASE'-or!$candidate.files.Count-or$candidate.files.Count-gt4096){throw 'Development candidate required'}
$payload=Join-Path $packageRoot 'payload'
$seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach($file in $candidate.files){
    Check-Time
    if(!$seen.Add($file.path)-or$file.sha256-notmatch'^[a-fA-F0-9]{64}$'){throw 'Duplicate/invalid hash entry'}
    $source=Relative-File $payload $file.path
    $item=Get-Item -LiteralPath $source
    if($item.Length-ne$file.size-or(Get-FileHash -LiteralPath $source).Hash-ne$file.sha256){throw ('Candidate changed: '+$file.path)}
}
if(@(Get-ChildItem -LiteralPath $payload -Recurse -File).Count-ne$seen.Count){throw 'Unlisted payload file'}
if($Action-eq'Validate'){Write-Output ('VALIDATED_NOT_EXECUTED: '+$seen.Count+' files');exit 0}
$admin=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if(!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator installation required'}
if((Test-Path $root)-or(Test-Path $data)-or(Test-Path $registry)){throw 'Existing installation/state: use an isolated clean test environment'}
# No start/auto-start: runtime Arm remains a separate reviewed gate.
New-Item -ItemType Directory -Path $root|Out-Null;Protect $root $true
New-Item -ItemType Directory -Path $data|Out-Null;Protect $data $false
$owner=@{id=$OwnerId;kind=$candidate.kind;root=$root;data=$data;candidateSha256=(Get-FileHash $manifestPath).Hash}
[IO.File]::WriteAllText($marker,($owner|ConvertTo-Json),$utf8)
[IO.File]::WriteAllText((Join-Path $data 'dev-owner.json'),($owner|ConvertTo-Json),$utf8)
foreach($file in $candidate.files){
    Check-Time;$source=Relative-File $payload $file.path;$destination=Relative-File $root $file.path
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force|Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
    if((Get-FileHash -LiteralPath $destination).Hash-ne$file.sha256){throw 'Installed candidate hash mismatch'}
}
No-Link $root
# Set protected ownership for all copied dependencies; no user-writable SYSTEM executables.
Native 'icacls.exe' @($root,'/setowner','*S-1-5-32-544','/T')
Native 'sc.exe' @('create',$service,'binPath=',('"'+(Join-Path $root 'RemoteAssist.Host.Service.exe')+'"'),'start=','demand','obj=','LocalSystem')
Write-Output 'INSTALLED_NOT_STARTED_NOT_E2E_PASS'
