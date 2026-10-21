param([Parameter(Mandatory=$true)][string]$Certificate,[Parameter(Mandatory=$true)][string]$Sha256)
$ErrorActionPreference='Stop'
# Read-only trust validation. Never add a root, accept a custom validation
# callback, ignore hostname errors, or launch a browser with TLS checks disabled.
if($Sha256-notmatch'^[a-fA-F0-9]{64}$'-or(Get-FileHash -LiteralPath $Certificate).Hash-ne$Sha256){throw 'TLS certificate changed'}
$certificateObject=New-Object Security.Cryptography.X509Certificates.X509Certificate2($Certificate)
$chain=New-Object Security.Cryptography.X509Certificates.X509Chain
try{
 $chain.ChainPolicy.RevocationMode=[Security.Cryptography.X509Certificates.X509RevocationMode]::Online
 $chain.ChainPolicy.VerificationFlags=[Security.Cryptography.X509Certificates.X509VerificationFlags]::NoFlag
 $chain.ChainPolicy.UrlRetrievalTimeout=[TimeSpan]::FromSeconds(2)
 if(!$chain.Build($certificateObject)){throw 'Existing certificate chain is not trusted'}
 if($certificateObject.NotBefore.ToUniversalTime()-gt[DateTime]::UtcNow-or$certificateObject.NotAfter.ToUniversalTime()-le[DateTime]::UtcNow){throw 'TLS certificate validity'}
 @{status='completed';chainValid=$true;storeMutated=$false}|ConvertTo-Json -Compress
}finally{$chain.Dispose();$certificateObject.Dispose()}
