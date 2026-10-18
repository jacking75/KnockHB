$ErrorActionPreference='Stop'
if($Staging-notmatch'^C:/Users/jacking75NoteBook/RemoteAssist-E2E-[0-9]+$'){throw 'Explicit staging required'}
$archive=Join-Path $Staging 'prepared.zip'
$expectedHash='a7b0dc993f9f5b40f912a87815f373e0733649e7b6a8d301703d3ee2b88ef956'
if((Get-FileHash $archive).Hash-ne$expectedHash){throw 'Product harness archive mismatch'}
$source=Join-Path $Staging 'tools';Expand-Archive -LiteralPath $archive -DestinationPath $source
$id=[guid]::NewGuid().ToString('N')
$root=Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$id)
New-Item -ItemType Directory -Path $root|Out-Null
$acl=New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true,$false);$acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)),'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-545')),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -LiteralPath $root -AclObject $acl
Copy-Item -LiteralPath (Join-Path $source 'scripts') -Destination $root -Recurse
Copy-Item -LiteralPath (Join-Path $source 'lan-test.cer') -Destination $root
$package=Join-Path $source 'candidate'
$candidateHash='2542625ec94b42ee95995a5a6299ad65d55ae37f6c73438693b92c05d691fc63'
if((Get-FileHash (Join-Path $package 'candidate.json')).Hash-ne$candidateHash){throw 'Prepared candidate mismatch'}
if((Test-Path "$env:ProgramFiles\RemoteAssist-Dev")-or(Test-Path "$env:ProgramData\RemoteAssist")-or(Get-Service RemoteAssistHost -ErrorAction SilentlyContinue)){throw 'Clean product environment required'}
$out=Join-Path $Staging 'observer';New-Item -ItemType Directory -Path $out|Out-Null
$plan=@{id=$id;profile='lan-view-consent-stop';review='PENDING';package=$package;candidateSha256=$candidateHash;output=$out}
$plan|ConvertTo-Json|Set-Content (Join-Path $root 'product-plan.json') -Encoding UTF8
$task='RemoteAssist-E2E-Product-'+$id;$observer='RemoteAssist-E2E-Observe-'+$id
$shell=Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$action=New-ScheduledTaskAction -Execute $shell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'scripts/e2e-product-guard.ps1')+'" -RunDir "'+$root+'"')
$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
$settings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 540) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $task -Action $action -Principal $principal -Settings $settings|Out-Null
$observeAction=New-ScheduledTaskAction -Execute $shell -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+(Join-Path $root 'scripts/e2e-console-observer.ps1')+'" -RunDir "'+$root+'" -OutputDir "'+$out+'"')
$user=New-ScheduledTaskPrincipal -UserId ($env:COMPUTERNAME+'\jacking75NoteBook') -LogonType Interactive -RunLevel Limited
$observeSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds 300) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
Register-ScheduledTask -TaskName $observer -Action $observeAction -Principal $user -Settings $observeSettings|Out-Null
@{id=$id;root=$root;task=$task;observer=$observer;output=$out}|ConvertTo-Json|Set-Content (Join-Path $Staging 'product-owner.json') -Encoding UTF8
@{state='PREPARED_NOT_STARTED';root=$root;task=$task;observer=$observer}|ConvertTo-Json
