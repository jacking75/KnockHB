param([Parameter(Mandatory)][uri]$Endpoint,[Parameter(Mandatory)][string]$CredentialFile)
$ErrorActionPreference='Stop'
if($Endpoint.Scheme-ne'https'-or$Endpoint.UserInfo-or$Endpoint.Query-or$Endpoint.Fragment-or$Endpoint.AbsolutePath-ne'/audit'){throw 'Fixed HTTPS /audit endpoint required'}
$identity=[Security.Principal.WindowsIdentity]::GetCurrent();if(!(New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required'}
$data=Join-Path $env:ProgramData 'RemoteAssist';if(!(Test-Path -LiteralPath $data)){throw 'Install protected Host first'}
$source=(Resolve-Path -LiteralPath $CredentialFile).Path;if((Get-Item -LiteralPath $source).Length-gt256){throw 'Credential size limit'};$secret=[IO.File]::ReadAllText($source).Trim();if($secret-notmatch'^[A-Za-z0-9_-]{32,128}$'){throw '32..128 base64url credential required'}
$key=Join-Path $data 'node.secret';$utf8=New-Object Text.UTF8Encoding($false);[IO.File]::WriteAllText($key,$secret,$utf8)
& "$env:SystemRoot/System32/icacls.exe" $data /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F'|Out-Null;if($LASTEXITCODE-ne0){throw 'Data ACL failed'}
& "$env:SystemRoot/System32/icacls.exe" $key /inheritance:r /grant:r '*S-1-5-18:F' '*S-1-5-32-544:F' /setowner '*S-1-5-32-544'|Out-Null;if($LASTEXITCODE-ne0){throw 'Credential ACL failed'}
$policy=Join-Path $data 'audit.settings.json';$temp=$policy+'.'+[guid]::NewGuid().ToString('N');[IO.File]::WriteAllText($temp,(@{Endpoint=$Endpoint.AbsoluteUri;CredentialFile=$key}|ConvertTo-Json),$utf8);if(Test-Path -LiteralPath $policy){[IO.File]::Replace($temp,$policy,$null)}else{[IO.File]::Move($temp,$policy)}
Write-Output 'Protected audit reporting configured. Restart the Host service only when no support session is active.'
