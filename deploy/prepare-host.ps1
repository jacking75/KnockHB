[CmdletBinding()]
param(
    [ValidateSet('Prepare','Verify','Commit','Rollback','Remove')][string]$Action='Verify',
    [string]$InstallationRoot=$PSScriptRoot,
    [switch]$LibraryOnly
)
$ErrorActionPreference='Stop'
$OutputEncoding=[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
$script:RAFirewallGroup='RemoteAssist.Startup.v1'

function Get-RAFirewallPlan([string]$Root,[int]$SignalingPort) {
    if($SignalingPort-lt1-or$SignalingPort-gt65535){throw 'Invalid signaling port'}
    $rootPath=[IO.Path]::GetFullPath($Root).TrimEnd('\')
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$tag=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($rootPath.ToLowerInvariant())))).Replace('-','').Substring(0,24)}finally{$sha.Dispose()}
    $tcpPorts=(@(443,$SignalingPort)|Sort-Object -Unique)-join','
    @(
        @{Suffix='MediaIn';File='RemoteAssist.Host.Helper.exe';Direction='Inbound';Protocol='UDP';LocalPort='27002-27099';RemotePort='Any'},
        @{Suffix='MediaOut';File='RemoteAssist.Host.Helper.exe';Direction='Outbound';Protocol='UDP';LocalPort='27002-27099';RemotePort='Any'},
        @{Suffix='SignalOut';File='RemoteAssist.Host.Helper.exe';Direction='Outbound';Protocol='TCP';LocalPort='Any';RemotePort=$tcpPorts},
        @{Suffix='ServiceOut';File='RemoteAssist.Host.Service.exe';Direction='Outbound';Protocol='TCP';LocalPort='Any';RemotePort=$tcpPorts}
    )|ForEach-Object {
        [pscustomobject]@{Name="RemoteAssist-$tag-$($_.Suffix)";Program=(Join-Path $rootPath $_.File);Direction=$_.Direction;Protocol=$_.Protocol;LocalPort=$_.LocalPort;RemotePort=$_.RemotePort}
    }
}

function Assert-RAFirewallRule($Rule,$Spec) {
    if(!$Rule-or$Rule.Group-ne$script:RAFirewallGroup-or[string]$Rule.Enabled-ne'True'-or[string]$Rule.Action-ne'Allow'-or[string]$Rule.Direction-ne$Spec.Direction-or[string]$Rule.Profile-ne'Any'){throw "Firewall rule mismatch: $($Spec.Name)"}
    $app=$Rule|Get-NetFirewallApplicationFilter
    $port=$Rule|Get-NetFirewallPortFilter
    $address=$Rule|Get-NetFirewallAddressFilter
    if([Environment]::ExpandEnvironmentVariables($app.Program)-ne$Spec.Program-or[string]$port.Protocol-ne$Spec.Protocol-or(($port.LocalPort)-join',')-ne$Spec.LocalPort-or(($port.RemotePort)-join',')-ne$Spec.RemotePort-or(($address.LocalAddress)-join',')-ne'Any'-or(($address.RemoteAddress)-join',')-ne'Any'){throw "Firewall filter mismatch: $($Spec.Name)"}
}

function Assert-RAFirewallPolicy($Plan) {
    # Check effective policy, not just the locally persisted allow rules.
    $profiles=@(Get-NetFirewallProfile -PolicyStore ActiveStore)
    if(!$profiles.Count){throw 'Windows Firewall effective policy unavailable'}
    foreach($profile in $profiles){
        if([string]$profile.Enabled-eq'True'-and([string]$profile.AllowInboundRules-eq'False'-or[string]$profile.AllowLocalFirewallRules-eq'False')){throw "Firewall policy prevents local preparation: $($profile.Name)"}
    }
    $programs=@($Plan|Select-Object -ExpandProperty Program -Unique)
    foreach($blocked in @(Get-NetFirewallRule -PolicyStore ActiveStore | Where-Object {[string]$_.Enabled-eq'True'-and[string]$_.Action-eq'Block'})){
        $app=$blocked|Get-NetFirewallApplicationFilter
        if([Environment]::ExpandEnvironmentVariables($app.Program)-in$programs){throw "Existing block rule for RemoteAssist: $($blocked.Name)"}
    }
}

function Remove-RAOwnedRule($Spec) {
    $rule=Get-NetFirewallRule -PolicyStore PersistentStore -Name $Spec.Name -ErrorAction SilentlyContinue
    if(!$rule){return}
    # Never delete a colliding rule belonging to a different application/group.
    if($rule.Group-ne$script:RAFirewallGroup-or($rule|Get-NetFirewallApplicationFilter).Program-ne$Spec.Program){throw "Firewall ownership mismatch: $($Spec.Name)"}
    $rule|Remove-NetFirewallRule
}

function Invoke-RAFirewallPreparation($Plan,[string]$Mode,[string]$Journal) {
    if($Mode-eq'Remove'){foreach($spec in $Plan){Remove-RAOwnedRule $spec};return}
    if($Mode-eq'Commit'){if(Test-Path -LiteralPath $Journal){Remove-Item -LiteralPath $Journal};return}
    if($Mode-eq'Rollback'){
        if(!(Test-Path -LiteralPath $Journal)){return}
        $saved=Get-Content -LiteralPath $Journal -Raw -Encoding UTF8|ConvertFrom-Json
        foreach($name in $saved.Created){
            $spec=@($Plan|Where-Object {$_.Name-eq$name})
            if($spec.Count-ne1){throw 'Firewall journal does not match installation'}
            Remove-RAOwnedRule $spec[0]
        }
        Remove-Item -LiteralPath $Journal
        return
    }
    if($Mode-eq'Prepare'-and(Test-Path -LiteralPath $Journal)){throw 'Pending firewall preparation requires rollback before retry'}
    Assert-RAFirewallPolicy $Plan
    $created=New-Object 'Collections.Generic.List[string]'
    try{
        if($Mode-eq'Prepare'){
            foreach($spec in $Plan){
                $existing=Get-NetFirewallRule -PolicyStore PersistentStore -Name $spec.Name -ErrorAction SilentlyContinue
                if($existing){Assert-RAFirewallRule $existing $spec;continue}
                # Persist intent before mutation so interrupted preparation can be rolled back.
                $created.Add($spec.Name)
                $temp=$Journal+'.tmp'
                [IO.File]::WriteAllText($temp,(@{Created=@($created)}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
                if(Test-Path -LiteralPath $Journal){[IO.File]::Replace($temp,$Journal,[NullString]::Value)}else{[IO.File]::Move($temp,$Journal)}
                New-NetFirewallRule -PolicyStore PersistentStore -Name $spec.Name -DisplayName $spec.Name -Group $script:RAFirewallGroup -Program $spec.Program -Direction $spec.Direction -Action Allow -Enabled True -Profile Any -Protocol $spec.Protocol -LocalPort $spec.LocalPort -RemotePort $spec.RemotePort -LocalAddress Any -RemoteAddress Any -EdgeTraversalPolicy Block|Out-Null
            }
        }
        foreach($spec in $Plan){Assert-RAFirewallRule (Get-NetFirewallRule -PolicyStore ActiveStore -Name $spec.Name -ErrorAction Stop) $spec}
    }catch{
        $failure=$_
        if($Mode-eq'Prepare'){
            # Roll back only rules created by this invocation; retain journal on any cleanup failure.
            foreach($name in $created){Remove-RAOwnedRule ($Plan|Where-Object {$_.Name-eq$name})}
            if(Test-Path -LiteralPath $Journal){Remove-Item -LiteralPath $Journal}
        }
        throw $failure
    }
}

if($LibraryOnly){return}
$principal=[Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if(!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Initial administrator launch required; no elevation during a support session.'}
$root=[IO.Path]::GetFullPath($InstallationRoot).TrimEnd('\')
$dev=Join-Path $env:ProgramFiles 'RemoteAssist-Dev'
$versions=(Join-Path $env:ProgramFiles 'RemoteAssist/versions')+'\'
if($root-ne$dev-and!$root.StartsWith($versions,[StringComparison]::OrdinalIgnoreCase)){throw 'Protected RemoteAssist installation required'}
for($item=Get-Item -LiteralPath $root -Force;$item-and$item.FullName-ne$env:ProgramFiles;$item=$item.Parent){
    if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse installation refused'}
}
foreach($name in @('Service','Tray','Helper','AdminHelper','RunbookWorker')){
    $exe=Get-Item -LiteralPath (Join-Path $root "RemoteAssist.Host.$name.exe") -Force
    if($exe.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse helper refused'}
}
$settings=Get-Content -LiteralPath (Join-Path $root 'host.settings.json') -Raw -Encoding UTF8|ConvertFrom-Json
$endpoint=[uri]$settings.SignalingUri
if(!$endpoint.IsAbsoluteUri-or$endpoint.Scheme-ne'https'){throw 'HTTPS signaling endpoint required for installed Host'}
$plan=@(Get-RAFirewallPlan $root $endpoint.Port)
$sas=Join-Path $root 'prepare-sas.ps1'
if(!(Test-Path -LiteralPath $sas)){throw 'SAS startup preparation script missing'}
if($Action-eq'Prepare'){
    try{
        & $sas -Action Prepare | Out-Null
        Invoke-RAFirewallPreparation $plan $Action (Join-Path $root 'startup-firewall.pending.json')
    }catch{
        $failure=$_
        & $sas -Action Rollback | Out-Null
        throw $failure
    }
}else{
    # Shared SAS state stays committed across versions; only full removal restores it.
    Invoke-RAFirewallPreparation $plan $Action (Join-Path $root 'startup-firewall.pending.json')
    & $sas -Action $Action | Out-Null
}
@{state=if($Action-eq'Verify'){'PREREQUISITES_VERIFIED_NOT_FUNCTION_PASS'}else{$Action.ToUpperInvariant()};rules=@($plan.Name)}|ConvertTo-Json -Compress
