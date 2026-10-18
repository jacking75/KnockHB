$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$certPath=Join-Path $repo '.artifacts/lan-tls-20261018/lan-test.cer'
if((Get-FileHash $certPath).Hash-ne'45EEBA84A6D2E5E30EC083C4E1981F6152C25625A0FF28E4F1E314A70087AED2'){throw 'Certificate changed'}
$certificate=New-Object Security.Cryptography.X509Certificates.X509Certificate2($certPath)
$store=New-Object Security.Cryptography.X509Certificates.X509Store('Root','CurrentUser');$store.Open('ReadOnly')
$existed=[bool]@($store.Certificates|Where-Object {$_.Thumbprint-eq$certificate.Thumbprint}).Count;$store.Close()
$owner=Join-Path $repo '.artifacts/tls-trust-preflight-owner.json'
if(Test-Path $owner){throw 'Existing trust preparation record; inspect before another attempt'}
@{thumb=$certificate.Thumbprint;added=!$existed;state='PREPARING';utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content $owner -Encoding UTF8
if(!$existed){
 $code='$ErrorActionPreference="Stop";$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2("'+$certPath+'");$store=New-Object Security.Cryptography.X509Certificates.X509Store("Root","CurrentUser");$store.Open("ReadWrite");try{$store.Add($cert)}finally{$store.Close()}'
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
 $process=[E2eOwnedCommand]::Start((Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'),@('-NoProfile','-EncodedCommand',$encoded))
 $deadline=[E2eOwnedCommand]::Deadline(120000)
 try{
  while(!$process.HasExited-and[E2eOwnedCommand]::Remaining($deadline)-gt0){Start-Sleep -Milliseconds 100}
 }finally{$process.Dispose()}
}
$store.Open('ReadOnly');$present=[bool]@($store.Certificates|Where-Object {$_.Thumbprint-eq$certificate.Thumbprint}).Count;$store.Close()
@{thumb=$certificate.Thumbprint;added=(!$existed-and$present);state=$(if($present){'READY'}else{'NOT_INSTALLED'});utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content $owner -Encoding UTF8
@{state=$(if($present){'TLS_TRUST_READY'}else{'TLS_TRUST_NOT_INSTALLED'});temporary=$true;productStarted=$false}|ConvertTo-Json
if(!$present){exit 1}
