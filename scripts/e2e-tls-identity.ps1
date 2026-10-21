param([Parameter(Mandatory=$true)][string]$Certificate,[Parameter(Mandatory=$true)][string]$Sha256,[switch]$ProbeEndpoint)
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
 $peerMatched=$false
 if($ProbeEndpoint){
  $tcp=New-Object Net.Sockets.TcpClient
  try{
   if(!$tcp.ConnectAsync('192.168.0.61',25001).Wait(3000)){throw 'TLS TCP deadline'}
   # Default platform certificate policy: chain AND hostname. No callback bypass.
   $ssl=New-Object Net.Security.SslStream($tcp.GetStream(),$false)
   try{
    if(!$ssl.AuthenticateAsClientAsync('192.168.0.61').Wait(5000)){throw 'TLS handshake deadline'}
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$actual=([BitConverter]::ToString($sha.ComputeHash($ssl.RemoteCertificate.GetRawCertData()))).Replace('-','')}finally{$sha.Dispose()}
    if($actual-ne$Sha256){throw 'TLS peer certificate changed'}
    $peerMatched=$true
   }finally{$ssl.Dispose()}
  }finally{$tcp.Dispose()}
 }
 @{status='completed';chainValid=$true;storeMutated=$false;endpointProbed=[bool]$ProbeEndpoint;hostnameValid=$peerMatched;peerCertificateMatched=$peerMatched;certificateSha256=$Sha256.ToLowerInvariant();sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;session=(Get-Process -Id $PID).SessionId;addresses=@(Get-NetIPAddress -AddressFamily IPv4|ForEach-Object {$_.IPAddress});tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency}|ConvertTo-Json -Compress
}finally{$chain.Dispose();$certificateObject.Dispose()}
