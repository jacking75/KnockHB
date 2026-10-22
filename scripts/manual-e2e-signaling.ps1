$ErrorActionPreference='Stop'
$root='F:\github\KnockHB'
$out=Join-Path $root '.artifacts/remote-pc-e2e/preparation-20261022-2243'
if(Get-NetTCPConnection -LocalPort 25001 -State Listen -ErrorAction SilentlyContinue){throw 'Port already in use'}
$secret=New-Object byte[] 48
$rng=[Security.Cryptography.RandomNumberGenerator]::Create();$rng.GetBytes($secret);$rng.Dispose()
[IO.File]::WriteAllText("$out/operator-secret",[Convert]::ToBase64String($secret).Replace('+','-').Replace('/','_').TrimEnd('='))
$env:RA_MODE='lan-test';$env:RA_LISTEN='192.168.0.61:25001';$env:RA_ALLOWED_ORIGINS='192.168.0.61:25001'
$env:RA_TLS_CERT="$root/.artifacts/lan-tls-20261018/server-cert.pem";$env:RA_TLS_KEY="$root/.artifacts/lan-tls-20261018/server-key.pem"
$env:RA_VIEWER_DIR="$root/web/viewer";$env:RA_OPERATOR_SECRET_FILE="$out/operator-secret";$env:RA_AUDIT_DIR="$out/audit"
$p=Start-Process "$root/.artifacts/c01-readiness-20261022-2158/signaling.exe" -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/signaling.out" -RedirectStandardError "$out/signaling.err"
@{pid=$p.Id;start=$p.StartTime.ToUniversalTime().ToString('o');image=$p.Path}|ConvertTo-Json|Set-Content "$out/signaling-owner.json" -Encoding UTF8
try {if(!$p.WaitForExit(3600000)){$p.Kill();$p.WaitForExit()}} finally {if(!$p.HasExited){$p.Kill()}}
