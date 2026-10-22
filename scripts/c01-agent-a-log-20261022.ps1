$ErrorActionPreference='Stop'
$exe='F:/github/DesktopTestDriver/bin/live_Release/Agent/Agent.exe'
$config='F:/github/KnockHB/.artifacts/dtd-e2e-20261020/Agent.json'
$root='F:/github/KnockHB/.artifacts/c01-readiness-20261022-2158'
if(Get-Process Agent -ErrorAction SilentlyContinue){throw 'Existing Agent requires inspection'}
$id=[guid]::NewGuid().ToString('N')
$p=Start-Process -FilePath $exe -ArgumentList @('--config',('"'+$config+'"'),'--serve') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $root ('agent-a-'+$id+'.out')) -RedirectStandardError (Join-Path $root ('agent-a-'+$id+'.err'))
$p.WaitForExit()
[IO.File]::WriteAllText((Join-Path $root ('agent-a-'+$id+'.exit.json')),(@{pid=$p.Id;exitCode=$p.ExitCode;utc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json))
exit $p.ExitCode
