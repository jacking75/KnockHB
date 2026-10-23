$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../deploy/prepare-host.ps1') -LibraryOnly
# Entire firewall backend is replaced. This test never calls Windows Firewall or starts a service.
$script:rules=@{};$script:created=0;$script:failAt=0;$script:hidden=$false;$script:blocked=$false
function Get-NetFirewallProfile {param($PolicyStore);[pscustomobject]@{Name='Private';Enabled='True';AllowInboundRules=$(if($script:blocked){'False'}else{'True'});AllowLocalFirewallRules='True'}}
function Get-NetFirewallRule { [CmdletBinding()]param($PolicyStore,$Name,$Enabled,$Action)
    if($Name){if($PolicyStore-eq'ActiveStore'-and$script:hidden){throw 'Effective rule missing'};return $script:rules[$Name]}
    @($script:rules.Values|Where-Object {!$Action-or$_.Action-eq$Action})
}
function Get-NetFirewallApplicationFilter {param([Parameter(ValueFromPipeline=$true)]$InputObject);process{[pscustomobject]@{Program=$InputObject.Program}}}
function Get-NetFirewallPortFilter {param([Parameter(ValueFromPipeline=$true)]$InputObject);process{[pscustomobject]@{Protocol=$InputObject.Protocol;LocalPort=$InputObject.LocalPort;RemotePort=$InputObject.RemotePort}}}
function Get-NetFirewallAddressFilter {param([Parameter(ValueFromPipeline=$true)]$InputObject);process{[pscustomobject]@{LocalAddress='Any';RemoteAddress='Any'}}}
function New-NetFirewallRule {param($PolicyStore,$Name,$DisplayName,$Group,$Program,$Direction,$Action,$Enabled,$Profile,$Protocol,$LocalPort,$RemotePort,$LocalAddress,$RemoteAddress,$EdgeTraversalPolicy)
    $script:created++
    if($script:created-eq$script:failAt){throw 'Injected rule creation failure'}
    $script:rules[$Name]=[pscustomobject]@{Name=$Name;Group=$Group;Program=$Program;Direction=$Direction;Action=$Action;Enabled=[string]$Enabled;Profile=$Profile;Protocol=$Protocol;LocalPort=$LocalPort;RemotePort=$RemotePort}
}
function Remove-NetFirewallRule {param([Parameter(ValueFromPipeline=$true)]$InputObject);process{$script:rules.Remove($InputObject.Name)}}
function Check($Condition,$Message){if(!$Condition){throw $Message}}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body}catch{$failed=$true};Check $failed 'Expected rejection'}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('RemoteAssist-startup-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp|Out-Null
$journal=Join-Path $temp 'pending.json'
try{
    $plan=@(Get-RAFirewallPlan 'C:\Program Files\RemoteAssist\versions\1.0.0.0' 25001)
    Check ($plan.Count-eq4) 'Missing network prerequisites'
    Check ($plan[0].LocalPort-eq'27002-27099'-and$plan[0].Program.EndsWith('RemoteAssist.Host.Helper.exe')) 'Media must be program/port scoped'
    Check ($plan[2].RemotePort-eq'443,25001') 'Tunnel ports differ'
    $other=@(Get-RAFirewallPlan 'C:\Program Files\RemoteAssist\versions\2.0.0.0' 25001)
    Check ($other[0].Name-ne$plan[0].Name) 'Version rules must not overwrite rollback rules'
    Invoke-RAFirewallPreparation $plan Prepare $journal
    Check ($script:rules.Count-eq4) 'Preparation incomplete'
    Invoke-RAFirewallPreparation $plan Verify $journal
    Check ($script:created-eq4) 'Verify mutated firewall'
    Invoke-RAFirewallPreparation $plan Commit $journal
    Invoke-RAFirewallPreparation $plan Prepare $journal
    Check ($script:created-eq4) 'Existing exact rules were recreated'
    Invoke-RAFirewallPreparation $plan Remove $journal
    Check ($script:rules.Count-eq0) 'Owned uninstall left rules'

    $script:created=0;$script:failAt=3
    Reject {Invoke-RAFirewallPreparation $plan Prepare $journal}
    Check ($script:rules.Count-eq0-and!(Test-Path $journal)) 'Partial failure did not roll back'
    $script:created=0;$script:failAt=0;$script:hidden=$true
    Reject {Invoke-RAFirewallPreparation $plan Prepare $journal}
    Check ($script:rules.Count-eq0) 'Effective policy failure left preparation rules'
    $script:hidden=$false;$script:blocked=$true;$script:created=0
    Reject {Invoke-RAFirewallPreparation $plan Prepare $journal}
    Check ($script:created-eq0) 'Blocked policy was mutated'
    $script:blocked=$false
    Invoke-RAFirewallPreparation $plan Prepare $journal
    $script:rules[$plan[0].Name].LocalPort='Any'
    Reject {Invoke-RAFirewallPreparation $plan Verify $journal}
    Invoke-RAFirewallPreparation $plan Rollback $journal
    Check ($script:rules.Count-eq0) 'Pending startup rollback failed'

    $script:rules[$plan[0].Name]=[pscustomobject]@{Name=$plan[0].Name;Group='SomeoneElse';Program='C:\Other.exe';Action='Allow'}
    Reject {Invoke-RAFirewallPreparation $plan Prepare $journal}
    Check ($script:rules.Count-eq1) 'Foreign colliding rule changed'
    Write-Output 'PASS startup preparation: scoped rules, idempotence, verify-only, policy denial, partial rollback, tampering, ownership'
}finally{
    $resolved=(Resolve-Path -LiteralPath $temp).Path
    if($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)-and(Split-Path $resolved -Leaf)-like'RemoteAssist-startup-test-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
