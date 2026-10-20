param([Parameter(Mandatory=$true)][ValidateSet('Inspect','Add','Remove')][string]$Action,[Parameter(Mandatory=$true)][string]$RunDir,[Parameter(Mandatory=$true)][string]$CertificateSha256,[ValidateSet('LocalMachine','CurrentUser')][string]$StoreScope='LocalMachine')
$ErrorActionPreference='Stop'
$certPath=Join-Path $RunDir 'lan-test.cer'
if($CertificateSha256-notmatch'^[a-fA-F0-9]{64}$'-or(Get-FileHash -LiteralPath $certPath).Hash-ne$CertificateSha256){throw 'Certificate hash changed'}
$cert=New-Object Security.Cryptography.X509Certificates.X509Certificate2($certPath)
$store=New-Object Security.Cryptography.X509Certificates.X509Store('Root',$StoreScope)
$ownerPath=Join-Path $RunDir 'tls-owner.json'
try{
 $store.Open($(if($Action-eq'Inspect'){'ReadOnly'}else{'ReadWrite'}))
 $present=@($store.Certificates|Where-Object {$_.Thumbprint-eq$cert.Thumbprint}).Count-gt0
 if($Action-eq'Add'){
  if(Test-Path -LiteralPath $ownerPath){throw 'Certificate add replay refused'}
  $bytes=(New-Object Text.UTF8Encoding($false)).GetBytes((@{added=!$present;thumb=$cert.Thumbprint;store=$StoreScope}|ConvertTo-Json))
  $stream=New-Object IO.FileStream($ownerPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
  try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
  if(!$present){$store.Add($cert)}
 }elseif($Action-eq'Remove'-and(Test-Path -LiteralPath $ownerPath)){
  $owner=[IO.File]::ReadAllText($ownerPath,[Text.Encoding]::UTF8)|ConvertFrom-Json
  if($owner.thumb-ne$cert.Thumbprint-or$owner.store-ne$StoreScope){throw 'Trust owner mismatch'}
  if($owner.added){foreach($entry in @($store.Certificates|Where-Object {$_.Thumbprint-eq$cert.Thumbprint})){$store.Remove($entry)}}
 }
 $present=@($store.Certificates|Where-Object {$_.Thumbprint-eq$cert.Thumbprint}).Count-gt0
 $result=Join-Path $RunDir 'tls-status.json';$temp=$result+'.'+$PID+'.tmp'
 [IO.File]::WriteAllText($temp,(@{thumb=$cert.Thumbprint;store=$StoreScope;present=$present;tick=[Diagnostics.Stopwatch]::GetTimestamp()}|ConvertTo-Json),(New-Object Text.UTF8Encoding($false)))
 if(Test-Path $result){[IO.File]::Replace($temp,$result,[NullString]::Value)}else{[IO.File]::Move($temp,$result)}
}finally{$store.Close();$cert.Dispose()}
