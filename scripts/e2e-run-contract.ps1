# Shared contract. Loading this file performs no product, task, certificate or GUI action.
Set-StrictMode -Version 2
function Assert-E2ePath([string]$Path) {
    $full=[IO.Path]::GetFullPath($Path)
    for($item=Get-Item -LiteralPath $full -Force -ErrorAction Stop;$item;){
        if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'E2E reparse path refused'}
        if($item -is [IO.FileInfo]){$item=$item.Directory}else{$item=$item.Parent}
    }
    return $full
}
function Enter-E2eJsonLock([string]$Path) {
    # Reader and writer must serialize the entire path-check/open/replace window.
    # Delete sharing alone does not prevent ReplaceFile's transient missing path.
    $hash=[Security.Cryptography.SHA256]::Create()
    try{$key=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Path).ToUpperInvariant())))).Replace('-','')}
    finally{$hash.Dispose()}
    $mutex=New-Object Threading.Mutex($false,('Local\RemoteAssist-E2E-Json-'+$key))
    try{
        $acquired=$false
        try{$acquired=$mutex.WaitOne(100)}catch [Threading.AbandonedMutexException]{$acquired=$true}
        if(!$acquired){throw 'E2E JSON lock deadline exceeded'}
        return $mutex
    }catch{$mutex.Dispose();throw}
}
function Read-E2eJson([string]$Path) {
    $mutex=Enter-E2eJsonLock $Path
    try{
        $null=Assert-E2ePath $Path
        if((Get-Item -LiteralPath $Path).Length-gt1048576){throw 'E2E JSON size bound'}
        $stream=New-Object IO.FileStream($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
        $reader=New-Object IO.StreamReader($stream,[Text.Encoding]::UTF8)
        try{return $reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()}
    }finally{$mutex.ReleaseMutex();$mutex.Dispose()}
}
function Write-E2eAtomicJson([string]$Path,$Value) {
    $mutex=Enter-E2eJsonLock $Path
    $temp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        $null=Assert-E2ePath ([IO.Path]::GetDirectoryName($Path))
        Write-E2eNewJson $temp $Value
        if([IO.File]::Exists($Path)){
            $null=Assert-E2ePath $Path
            [IO.File]::Replace($temp,$Path,[NullString]::Value)
        }else{[IO.File]::Move($temp,$Path)}
    }finally{
        if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)}
        $mutex.ReleaseMutex();$mutex.Dispose()
    }
}
function Get-E2eFailure($Record,[string]$Phase,[string]$Role,[bool]$Cleaning) {
    # Exception messages/stack text may contain private arguments. Preserve the
    # exception type chain, HResults and source location without those contents.
    $chain=@();$exception=$Record.Exception
    while($exception-and$chain.Count-lt8){
        $chain+=@(@{type=$exception.GetType().FullName;hresult=$exception.HResult})
        $exception=$exception.InnerException
    }
    return @{phase=$Phase;role=$Role;cleaning=$Cleaning;exceptions=$chain;
        script=[IO.Path]::GetFileName($Record.InvocationInfo.ScriptName);
        line=$Record.InvocationInfo.ScriptLineNumber;tick=[Diagnostics.Stopwatch]::GetTimestamp()}
}
function Read-E2eManifest([string]$Path,[string]$ExpectedSha256) {
    if($ExpectedSha256-notmatch'^[a-fA-F0-9]{64}$'){throw 'Explicit frozen manifest hash required'}
    $null=Assert-E2ePath $Path
    if((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash-ne$ExpectedSha256){throw 'Frozen manifest changed'}
    $m=Read-E2eJson $Path
    if($m.version-notin@(3,4)-or$m.id-notmatch'^[a-f0-9]{32}$'-or$m.id-eq'6a2f55ebaee74a4099177a271ea0b88c'-or$m.state-ne'PREPARED_NOT_AUTHORIZED') {throw 'Invalid selected campaign manifest'}
    if($m.version-eq3){if($m.profile-ne'lan-view-consent-stop'){throw 'Invalid legacy profile'}}
    else{
        if($m.caseId-notmatch'^C(0[1-9]|1[0-6])$'-or$m.profile-ne('e2e-'+$m.caseId)-or$m.tls.mode-ne'existing-chain'){throw 'Invalid case profile/TLS mode'}
        $masks=@(1,0,3,7,7,7,8,8,8,128,16,32,64,67,1,3)
        if($m.expectedMask-ne$masks[[int]$m.caseId.Substring(1)-1]){throw 'Case permission mask changed'}
    }
    if($m.budget.totalSeconds-ne900-or$m.budget.targetSeconds-ne600-or$m.budget.activeSeconds-ne300-or$m.budget.cleanupSeconds-ne240-or$m.budget.recoverySeconds-ne300-or$m.budget.reportSeconds-ne60){throw 'Unreviewed campaign budget'}
    if($m.archiveSha256-notmatch'^[a-fA-F0-9]{64}$'-or$m.candidateSha256-notmatch'^[a-fA-F0-9]{64}$'-or$m.tls.sha256-notmatch'^[a-fA-F0-9]{64}$'-or$m.tls.thumbprint-notmatch'^[a-fA-F0-9]{40}$'){throw 'Invalid campaign hashes'}
    $seen=New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $m.tools){
        if($entry.path-notmatch'^scripts/[A-Za-z0-9_.-]+$'-or$entry.sha256-notmatch'^[a-fA-F0-9]{64}$'-or!$seen.Add($entry.path)){throw 'Invalid or duplicate campaign tool'}
    }
    foreach($required in @('scripts/e2e-run-contract.ps1','scripts/e2e-product-guard.ps1','scripts/e2e-native.cs','scripts/install-host-dev.ps1','scripts/e2e-certificate.ps1','scripts/e2e-resource-inspect.ps1')){
        if(!$seen.Contains($required)){throw 'Required campaign tool missing'}
    }
    return $m
}
function Assert-E2eTools($Manifest,[string]$Root) {
    foreach($entry in $Manifest.tools){
        $path=Assert-E2ePath (Join-Path $Root $entry.path)
        if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash-ne$entry.sha256){throw 'Frozen campaign tool changed'}
    }
}
function Get-E2eOwner {
    if(!$script:CampaignManifest-or!$script:CampaignManifestSha256){throw 'Frozen manifest transport required; legacy pointer rejected'}
    $owner=Read-E2eJson (Join-Path $Staging 'product-owner.json')
    $expected=Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$CampaignManifest.id)
    if($owner.id-ne$CampaignManifest.id-or$owner.manifestSha256-ne$CampaignManifestSha256-or$owner.root-ne$expected-or$owner.task-ne('RemoteAssist-E2E-Product-'+$owner.id)-or$owner.watchdog-ne('RemoteAssist-E2E-Watchdog-'+$owner.id)-or$owner.observer-ne('RemoteAssist-E2E-Observe-'+$owner.id)-or$owner.output-ne(Join-Path $Staging 'observer')){throw 'Campaign owner mismatch'}
    $null=Assert-E2ePath $owner.root
    $protected=Read-E2eManifest (Join-Path $owner.root 'campaign.json') $CampaignManifestSha256
    Assert-E2eTools $protected $owner.root
    return $owner
}
function Write-E2eNewJson([string]$Path,$Value) {
    $bytes=(New-Object Text.UTF8Encoding($false)).GetBytes(($Value|ConvertTo-Json -Depth 12))
    $stream=New-Object IO.FileStream($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
}
# Same-PC QPC anchor. A remote grant must use a B sample taken before A's response receipt;
# adding A's remaining budget at receipt cannot extend A's global deadline. Never subtract UTCs.
function Get-E2eRemoteDeadline([long]$RemoteSample,[long]$RemoteFrequency,[double]$RemainingMilliseconds) {
    if($RemoteSample-le0-or$RemoteFrequency-le0-or$RemainingMilliseconds-le0-or$RemainingMilliseconds-gt900000){throw 'Invalid monotonic grant'}
    return [long]($RemoteSample+[Math]::Floor($RemainingMilliseconds*$RemoteFrequency/1000))
}
