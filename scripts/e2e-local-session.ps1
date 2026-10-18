param([Parameter(Mandatory=$true)][string]$RunDir)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$deadline=[E2eOwnedCommand]::Deadline(600000)
@{deadline=$deadline;tick=[E2eOwnedCommand]::Tick;frequency=[Diagnostics.Stopwatch]::Frequency;utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $RunDir 'local-start.json') -Encoding UTF8
$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2((Join-Path $repo '.artifacts/lan-tls-20261018/lan-test.cer'))
$store=New-Object Security.Cryptography.X509Certificates.X509Store('Root','CurrentUser')
$added=$false;$server=$null;$viewer=$null
$clock=[Diagnostics.Stopwatch]::StartNew()
try{
 $store.Open('ReadWrite');$exists=[bool]@($store.Certificates|Where-Object {$_.Thumbprint-eq$cert.Thumbprint}).Count
 $leasePath=Join-Path $repo '.artifacts/tls-trust-preflight-owner.json'
 $lease=if(Test-Path $leasePath){Get-Content $leasePath -Raw -Encoding UTF8|ConvertFrom-Json}else{$null}
 $ownedTrust=$lease-and$lease.state-eq'READY'-and$lease.added-and$lease.thumb-eq$cert.Thumbprint
 if(!$exists){throw 'TLS trust must be prepared before the product campaign'}
 $added=[bool]$ownedTrust
 @{thumb=$cert.Thumbprint;added=$added}|ConvertTo-Json|Set-Content (Join-Path $RunDir 'local-trust-owner.json') -Encoding UTF8
 $store.Close()
 $secret=New-Object byte[] 48;$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($secret);$rng.Dispose()
 [IO.File]::WriteAllText((Join-Path $RunDir 'operator-secret'),([Convert]::ToBase64String($secret).Replace('+','-').Replace('/','_').TrimEnd('=')))
 $env:RA_MODE='lan-test';$env:RA_LISTEN='192.168.0.61:25001';$env:RA_ALLOWED_ORIGINS='192.168.0.61:25001'
 $env:RA_TLS_CERT=Join-Path $repo '.artifacts/lan-tls-20261018/server-cert.pem';$env:RA_TLS_KEY=Join-Path $repo '.artifacts/lan-tls-20261018/server-key.pem'
 $env:RA_VIEWER_DIR=Join-Path $repo 'web/viewer';$env:RA_OPERATOR_SECRET_FILE=Join-Path $RunDir 'operator-secret';$env:RA_AUDIT_DIR=Join-Path $RunDir 'audit'
 $server=[E2eOwnedCommand]::Start((Join-Path $repo '.artifacts/signaling-dev.exe'),@())
 $ready=[E2eOwnedCommand]::Deadline(10000);$connected=$false
 while([E2eOwnedCommand]::Remaining($ready)-gt0){
  if($server.HasExited){throw 'Signaling exited'}
  try{$request=[Net.WebRequest]::Create('https://192.168.0.61:25001/healthz');$request.Timeout=1000;$response=$request.GetResponse();$response.Close();$connected=$true;break}catch{Start-Sleep -Milliseconds 100}
 }
 if(!$connected){throw 'Trusted local HTTPS failed'}
 $node=(Get-Command node.exe).Source
 $viewer=[E2eOwnedCommand]::Start($node,@((Join-Path $PSScriptRoot 'e2e-viewer.cjs'),$RunDir))
 while([E2eOwnedCommand]::Remaining($deadline)-gt0-and!(Test-Path (Join-Path $RunDir 'finish.json'))){
  if($server.HasExited){throw 'Signaling exited'}
  if($viewer.HasExited){break}
  Start-Sleep -Milliseconds 100
 }
}catch{@{error=$_.Exception.Message}|ConvertTo-Json|Set-Content (Join-Path $RunDir 'local-error.json') -Encoding UTF8}
finally{
 if($viewer){$viewer.Dispose()};if($server){$server.Dispose()}
 if($added){$store.Open('ReadWrite');foreach($entry in @($store.Certificates|Where-Object {$_.Thumbprint-eq$cert.Thumbprint})){$store.Remove($entry)};$store.Close()}
 @{seconds=$clock.Elapsed.TotalSeconds;ownedJobsClosed=$true;temporaryTrustRemoved=$added}|ConvertTo-Json|Set-Content (Join-Path $RunDir 'local-cleanup.json') -Encoding UTF8
}
