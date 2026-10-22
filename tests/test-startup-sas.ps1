$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../deploy/prepare-sas.ps1') -LibraryOnly
# Mock backend only: no real registry, services, SAS, desktop or firewall operations.
$script:policy=$null;$script:writes=0;$script:fail=$false
function Policy($Value){if($null-eq$Value){[pscustomobject]@{Exists=$false;Kind=$null;Value=$null}}else{[pscustomobject]@{Exists=$true;Kind='DWord';Value=$Value}}}
function Get-RASasPolicy {return $script:policy}
function Set-RASasPolicy($Policy){$script:writes++;if($script:fail){throw 'Injected policy write failure'};$script:policy=$Policy}
function Check($Condition,$Message){if(!$Condition){throw $Message}}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body}catch{$failed=$true};Check $failed 'Expected rejection'}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('RemoteAssist-sas-test-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temp)|Out-Null
$journal=Join-Path $temp 'sas.json'
try{
    $script:policy=Policy $null
    Reject {Invoke-RASasPreparation Verify $journal};Check ($script:writes-eq0) 'Verify mutated policy'
    Invoke-RASasPreparation Prepare $journal
    Check ($script:policy.Value-eq1-and(Test-Path $journal)) 'Unset policy not prepared'
    Reject {Invoke-RASasPreparation Prepare $journal}
    Invoke-RASasPreparation Verify $journal;Invoke-RASasPreparation Commit $journal
    Invoke-RASasPreparation Prepare $journal;Invoke-RASasPreparation Rollback $journal
    Check ($script:policy.Value-eq1-and$script:writes-eq1) 'Committed state changed on version prepare/rollback'
    Invoke-RASasPreparation Remove $journal
    Check (!$script:policy.Exists-and!(Test-Path $journal)) 'Original absent value not restored'
    $script:policy=Policy 2
    Invoke-RASasPreparation Prepare $journal
    Check ($script:policy.Value-eq3) 'Existing accessibility grant lost'
    Invoke-RASasPreparation Rollback $journal
    Check ($script:policy.Value-eq2) 'Rollback did not restore prior policy'
    foreach($value in @(1,3)){
        $script:policy=Policy $value;$before=$script:writes
        Invoke-RASasPreparation Prepare $journal;Invoke-RASasPreparation Commit $journal;Invoke-RASasPreparation Remove $journal
        Check ($script:policy.Value-eq$value-and$script:writes-eq$before-and!(Test-Path $journal)) 'Existing enabled policy claimed or changed'
    }
    foreach($value in @(0,4)){
        $script:policy=Policy $value;$before=$script:writes
        Reject {Invoke-RASasPreparation Prepare $journal}
        Check ($script:writes-eq$before-and!(Test-Path $journal)) 'Explicit policy denial overridden'
    }
    $script:policy=[pscustomobject]@{Exists=$true;Kind='String';Value='1'}
    Reject {Invoke-RASasPreparation Prepare $journal};Reject {Invoke-RASasPreparation Verify $journal}
    $script:policy=Policy 2
    Invoke-RASasPreparation Prepare $journal;Invoke-RASasPreparation Commit $journal
    $script:policy=Policy 0;$before=$script:writes
    Invoke-RASasPreparation Remove $journal
    Check ($script:policy.Value-eq0-and$script:writes-eq$before) 'Third-party replacement overwritten'
    $script:policy=Policy $null;$script:fail=$true
    Reject {Invoke-RASasPreparation Prepare $journal};$script:fail=$false
    Check (!$script:policy.Exists-and!(Test-Path $journal)) 'Failed write left owned grant'
    $script:policy=Policy $null
    Write-RASasJournal $journal ([pscustomobject]@{Version=1;State='pending';Original=(Policy $null);Applied=1})
    Invoke-RASasPreparation Rollback $journal
    Check (!(Test-Path $journal)-and!$script:policy.Exists) 'Crash before policy write not recoverable'
    [IO.File]::WriteAllText($journal,'{"Version":1,"State":"pending","Original":{"Exists":true,"Kind":"DWord","Value":0},"Applied":3}')
    Reject {Invoke-RASasPreparation Remove $journal};Remove-Item -LiteralPath $journal
    'PASS SAS preparation: verify-only, policy denial, idempotent versions, ownership, rollback, external change, invalid journal'
}finally{
    $full=[IO.Path]::GetFullPath($temp);$parent=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(!$full.StartsWith($parent,[StringComparison]::OrdinalIgnoreCase)-or!(Split-Path $full -Leaf).StartsWith('RemoteAssist-sas-test-')){throw 'Unsafe test cleanup'}
    Remove-Item -LiteralPath $full -Recurse -Force
}
