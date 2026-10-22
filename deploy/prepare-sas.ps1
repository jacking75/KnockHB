[CmdletBinding()]
param(
    [ValidateSet('Prepare','Verify','Commit','Rollback','Remove')][string]$Action='Verify',
    [switch]$LibraryOnly
)
$ErrorActionPreference='Stop'
$script:RASasNativeMode=$false
$script:RASasKey='SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
$script:RASasName='SoftwareSASGeneration'
function Get-RASasPolicy {
    $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($script:RASasKey,$false)
    try{
        if(!$key-or$key.GetValueNames()-notcontains$script:RASasName){return [pscustomobject]@{Exists=$false;Kind=$null;Value=$null}}
        [pscustomobject]@{Exists=$true;Kind=[string]$key.GetValueKind($script:RASasName);Value=$key.GetValue($script:RASasName)}
    }finally{if($key){$key.Dispose()}}
}
function Set-RASasPolicy($Policy) {
    $key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($script:RASasKey,$true)
    if(!$key){throw 'Windows system policy registry key missing'}
    try{
        if($Policy.Exists){$key.SetValue($script:RASasName,[int]$Policy.Value,[Microsoft.Win32.RegistryValueKind]::DWord)}
        else{$key.DeleteValue($script:RASasName,$false)}
    }finally{$key.Dispose()}
}
function Assert-RASasEnabled($Policy) {
    if(!$Policy.Exists-or$Policy.Kind-ne'DWord'-or$Policy.Value-notin@(1,3)){throw 'Service SAS policy is not prepared; complete initial administrator setup'}
}
function Read-RASasJournal([string]$Journal) {
    if(!(Test-Path -LiteralPath $Journal)){return $null}
    $record=Get-Content -LiteralPath $Journal -Raw -Encoding UTF8|ConvertFrom-Json
    if($record.Version-ne1-or$record.State-notin@('pending','committed')-or$record.Original.Exists-isnot[bool]-or$record.Applied-notin@(1,3)){throw 'Invalid SAS ownership journal'}
    if($record.Original.Exists){if($record.Original.Kind-ne'DWord'-or$record.Original.Value-ne2-or$record.Applied-ne3){throw 'Invalid SAS original policy'}}
    elseif($null-ne$record.Original.Kind-or$null-ne$record.Original.Value-or$record.Applied-ne1){throw 'Invalid SAS absent original policy'}
    return $record
}
function Write-RASasJournal([string]$Journal,$Record) {
    $temp=$Journal+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        [IO.File]::WriteAllText($temp,($Record|ConvertTo-Json -Depth 5 -Compress),[Text.UTF8Encoding]::new($false))
        if($script:RASasNativeMode){
            $acl=New-Object Security.AccessControl.FileSecurity
            $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)')
            Set-Acl -LiteralPath $temp -AclObject $acl
        }
        if(Test-Path -LiteralPath $Journal){[IO.File]::Replace($temp,$Journal,[NullString]::Value)}else{[IO.File]::Move($temp,$Journal)}
    }finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}
}
function Restore-RASasPolicy($Record,[string]$Journal) {
    $current=Get-RASasPolicy
    if($current.Exists-and$current.Kind-eq'DWord'-and$current.Value-eq$Record.Applied){Set-RASasPolicy $Record.Original}
    # A later administrator/policy change owns the value; never overwrite it.
    Remove-Item -LiteralPath $Journal -Force
}
function Invoke-RASasPreparation([string]$Mode,[string]$Journal) {
    $record=Read-RASasJournal $Journal
    if($Mode-eq'Rollback'){if($record-and$record.State-eq'pending'){Restore-RASasPolicy $record $Journal};return}
    if($Mode-eq'Remove'){if($record){Restore-RASasPolicy $record $Journal};return}
    $current=Get-RASasPolicy
    if($Mode-eq'Verify'){Assert-RASasEnabled $current;return}
    if($Mode-eq'Commit'){
        Assert-RASasEnabled $current
        if($record){if($current.Value-ne$record.Applied){throw 'SAS policy changed before commit'};$record.State='committed';Write-RASasJournal $Journal $record};return
    }
    if($Mode-ne'Prepare'){throw 'Invalid SAS preparation action'}
    if($record){
        if($record.State-eq'pending'){throw 'Pending SAS preparation must be rolled back before another installation'}
        Assert-RASasEnabled $current
        if($current.Value-ne$record.Applied){throw 'SAS policy differs from owned value'}
        return
    }
    if($current.Exists){
        if($current.Kind-ne'DWord'-or$current.Value-notin@(1,2,3)){throw 'Explicitly denied or unsupported SAS policy; do not override administrator policy'}
        if($current.Value-in@(1,3)){return}
    }
    $applied=if($current.Exists){3}else{1}
    $record=[pscustomobject]@{Version=1;State='pending';Original=$current;Applied=$applied}
    Write-RASasJournal $Journal $record
    try{
        Set-RASasPolicy ([pscustomobject]@{Exists=$true;Kind='DWord';Value=$applied})
        $verified=Get-RASasPolicy;Assert-RASasEnabled $verified
        if($verified.Value-ne$applied){throw 'SAS preparation was superseded by policy'}
    }catch{$failure=$_;Restore-RASasPolicy $record $Journal;throw $failure}
}
function Assert-RASasProtectedPath([string]$Path) {
    $item=Get-Item -LiteralPath $Path -Force
    if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse SAS state path refused'}
    $acl=Get-Acl -LiteralPath $Path
    $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if($owner-notin@('S-1-5-18','S-1-5-32-544')){throw 'SAS state owner must be SYSTEM or Administrators'}
    $write=[Security.AccessControl.FileSystemRights]::Write-bor[Security.AccessControl.FileSystemRights]::Delete-bor[Security.AccessControl.FileSystemRights]::ChangePermissions-bor[Security.AccessControl.FileSystemRights]::TakeOwnership-bor[Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles
    foreach($entry in $acl.Access){
        $sid=$entry.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
        if($entry.AccessControlType-eq'Allow'-and($entry.FileSystemRights-band$write)-and$sid-notin@('S-1-5-18','S-1-5-32-544')){throw 'SAS state must be writable only by SYSTEM and Administrators'}
    }
}
if($LibraryOnly){return}
$script:RASasNativeMode=$true
if(![Environment]::Is64BitProcess){throw 'SAS preparation requires 64-bit PowerShell'}
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Initial administrator launch required'}
$root=Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'RemoteAssist'
if(!(Test-Path -LiteralPath $root)){
    if($Action-eq'Prepare'){
        [IO.Directory]::CreateDirectory($root)|Out-Null
        $acl=New-Object Security.AccessControl.DirectorySecurity
        $acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;BU)')
        Set-Acl -LiteralPath $root -AclObject $acl
    }elseif($Action-in@('Remove','Rollback')){return}else{throw 'Protected installation state directory missing'}
}
Assert-RASasProtectedPath $root
$journal=Join-Path $root 'startup-sas.json'
if(Test-Path -LiteralPath $journal){Assert-RASasProtectedPath $journal}
Invoke-RASasPreparation $Action $journal
@{state=if($Action-eq'Verify'){'SAS_PREREQUISITE_VERIFIED_NOT_FUNCTION_PASS'}else{$Action.ToUpperInvariant()}}|ConvertTo-Json -Compress
