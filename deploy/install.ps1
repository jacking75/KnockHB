param([ValidateSet('Install','Recover','Remove')][string]$Action='Install',[Parameter(Mandatory=$true)][string]$Package)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'RemoteAssist'
$state=Join-Path $env:ProgramData 'RemoteAssist'
$utf8=New-Object Text.UTF8Encoding($false)
$serviceName='RemoteAssistHost'
function AtomicText($path,$text){$temp=$path+'.'+[guid]::NewGuid().ToString('N');try{$bytes=$utf8.GetBytes($text);$stream=New-Object IO.FileStream($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough);try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};if(Test-Path -LiteralPath $path){[IO.File]::Replace($temp,$path,$null)}else{[IO.File]::Move($temp,$path)}}finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}}
function Atomic($path,$value){AtomicText $path ($value|ConvertTo-Json -Depth 8)}
function Native($name,$arguments){& $name @arguments | Out-Null;if($LASTEXITCODE -ne 0){throw ('Native command failed: '+$name)}}
function Protect($path,$readUsers){$grants=@('*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F');if($readUsers){$grants+= '*S-1-5-32-545:(OI)(CI)RX'};Native "$env:SystemRoot\System32\icacls.exe" (@($path,'/inheritance:r','/grant:r')+$grants);Native "$env:SystemRoot\System32\icacls.exe" @($path,'/setowner','*S-1-5-32-544','/T')}
function SafeTree($path){$full=[IO.Path]::GetFullPath($path);if(!(Test-Path -LiteralPath $full)){return};for($p=Get-Item -LiteralPath $full;$p;$p=$p.Parent){if($p.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse installation path'}};foreach($item in Get-ChildItem -LiteralPath $full -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse installation item'}}}
function StopOwned(){if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){Stop-Service -Name $serviceName -ErrorAction Stop;(Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))}}
function SetImage($image){Native "$env:SystemRoot\System32\sc.exe" @('config',$serviceName,'binPath=',('"'+$image+'"'),'start=','auto')}
function WaitReady($image){$clock=[Diagnostics.Stopwatch]::StartNew();while($clock.Elapsed.TotalSeconds-lt45){if(Test-Path -LiteralPath (Join-Path $state 'ready.json')){$ready=Get-Content -LiteralPath (Join-Path $state 'ready.json') -Encoding UTF8|ConvertFrom-Json;$process=Get-Process -Id $ready.Pid -ErrorAction SilentlyContinue;if($process-and$process.Path-eq$image-and$ready.Image-eq$image){return}};$service=Get-Service $serviceName;if($service.Status-eq'Stopped'){throw 'New service stopped before readiness'};Start-Sleep -Milliseconds 250};throw 'New service readiness timeout'}
function Rollback($tx){StopOwned;if($tx.Previous){if(!$tx.Previous.StartsWith($root+'\versions\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe rollback image'};SetImage $tx.Previous;Remove-Item -LiteralPath (Join-Path $state 'ready.json') -Force -ErrorAction SilentlyContinue;Start-Service $serviceName;WaitReady $tx.Previous}elseif(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){Native "$env:SystemRoot\System32\sc.exe" @('delete',$serviceName)};Atomic (Join-Path $root 'transaction.json') @{Status='rolled-back';Previous=$tx.Previous;Candidate=$tx.Candidate}}
$admin=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent());if(!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required'}
SafeTree $root;SafeTree $state
if(!(Test-Path -LiteralPath $root)){New-Item -ItemType Directory -Path $root|Out-Null;Protect $root $true}
if(!(Test-Path -LiteralPath $state)){New-Item -ItemType Directory -Path $state|Out-Null;Protect $state $false}
$lock=New-Object IO.FileStream((Join-Path $root 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $idle=$null
    if($Action-ne'Install'){$idle=New-Object IO.FileStream((Join-Path $state 'session.gate'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)}
    $transaction=Join-Path $root 'transaction.json'
    if(Test-Path -LiteralPath $transaction){$pending=Get-Content -LiteralPath $transaction -Encoding UTF8|ConvertFrom-Json;if($pending.Status-eq'pending'){Rollback $pending}}
    if($Action-eq'Recover'){exit 0}
    if($Action-eq'Remove'){
        StopOwned;if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){Native "$env:SystemRoot\System32\sc.exe" @('delete',$serviceName)};foreach($name in @('RemoteAssist.Update','RemoteAssist.Recover')){Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue};$versions=Join-Path $root 'versions';SafeTree $versions;if(Test-Path -LiteralPath $versions){Remove-Item -LiteralPath $versions -Recurse -Force}
        Add-Type -TypeDefinition 'using System;using System.Runtime.InteropServices;public static class RemoteAssistRemoval{[DllImport("kernel32.dll",EntryPoint="MoveFileExW",CharSet=CharSet.Unicode,SetLastError=true)]public static extern bool Schedule(string path,string target,uint flags);}'
        foreach($name in @('RemoteAssist.Update.exe','install.ps1','update.policy.json','trusted-public.pem','highest.version','transaction.json','update.notice.json')){$owned=Join-Path $root $name;if(Test-Path -LiteralPath $owned){try{Remove-Item -LiteralPath $owned -Force -ErrorAction Stop}catch{if(![RemoteAssistRemoval]::Schedule($owned,$null,4)){throw 'Owned uninstall file could not be scheduled for reboot cleanup'}}}};exit 0
    }
    $manifest=Get-Content -LiteralPath (Join-Path $Package 'release.json') -Encoding UTF8|ConvertFrom-Json
    $stagePath=[IO.Path]::GetFullPath($Package)
    if(!$stagePath.StartsWith($state+'\staging\',[StringComparison]::OrdinalIgnoreCase)){throw 'Installer requires updater-verified protected staging'}
    if($manifest.Product-ne'RemoteAssist'-or$manifest.Platform-ne'windows-x64'-or$manifest.Protocol-ne1-or$manifest.Version-notmatch'^\d+\.\d+\.\d+\.\d+$'){throw 'Manifest contract'}
    SafeTree $Package
    foreach($f in $manifest.Files){if($f.Path-notmatch'^[A-Za-z0-9_./-]+$'-or$f.Path-match'(^|/)\.\.?(/|$)'){throw 'Manifest path'};$file=Join-Path $Package $f.Path;if((Get-Item -LiteralPath $file).Length-ne$f.Size-or(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()-ne$f.Sha256){throw 'Staging changed after verification'}}
    $highestPath=Join-Path $root 'highest.version';if(Test-Path -LiteralPath $highestPath){$highest=[version](Get-Content -LiteralPath $highestPath -Encoding UTF8);if([version]$manifest.Version-lt$highest){throw 'Downgrade prohibited'}}
    $candidate=Join-Path (Join-Path $root 'versions') $manifest.Version
    if(Test-Path -LiteralPath $candidate){foreach($f in $manifest.Files){$installed=Join-Path $candidate $f.Path;if(!(Test-Path -LiteralPath $installed)-or(Get-FileHash -LiteralPath $installed).Hash.ToLowerInvariant()-ne$f.Sha256){$candidate=Join-Path (Join-Path $root 'versions') ($manifest.Version+'-repair-'+[guid]::NewGuid().ToString('N'));break}}}
    if(!(Test-Path -LiteralPath $candidate)){New-Item -ItemType Directory -Path $candidate -Force|Out-Null;foreach($child in Get-ChildItem -LiteralPath $Package){Copy-Item -LiteralPath $child.FullName -Destination $candidate -Recurse};Protect $candidate $true}
    $image=Join-Path $candidate 'RemoteAssist.Host.Service.exe';$existing=Get-CimInstance Win32_Service -Filter "Name='RemoteAssistHost'"; $previous=$null
    if($existing){$previous=$existing.PathName.Trim('"');if(!$previous.StartsWith($root+'\versions\',[StringComparison]::OrdinalIgnoreCase)){throw 'Service name belongs to another installation'}}
    StopOwned
    foreach($file in @('RemoteAssist.Update.exe','install.ps1')){if(!(Test-Path -LiteralPath (Join-Path $root $file))){Copy-Item -LiteralPath (Join-Path $candidate $file) -Destination (Join-Path $root $file)}}
    if(!(Test-Path -LiteralPath (Join-Path $root 'trusted-public.pem'))){Copy-Item -LiteralPath (Join-Path $candidate 'trusted-public.pem') -Destination $root}
    $worker=Join-Path $root 'RemoteAssist.Update.exe';$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
    foreach($task in @(@{Name='RemoteAssist.Recover';Command='recover';Trigger=(New-ScheduledTaskTrigger -AtStartup)},@{Name='RemoteAssist.Update';Command='update';Trigger=(New-ScheduledTaskTrigger -Daily -At '03:00')})){Register-ScheduledTask -TaskName $task.Name -Action (New-ScheduledTaskAction -Execute $worker -Argument $task.Command) -Trigger $task.Trigger -Principal $principal -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(15))) -Force|Out-Null}
    Atomic $transaction @{Status='pending';Previous=$previous;Candidate=$image}
    try{
        if($existing){SetImage $image}else{Native "$env:SystemRoot\System32\sc.exe" @('create',$serviceName,'binPath=',('"'+$image+'"'),'start=','auto','obj=','LocalSystem')}
        Remove-Item -LiteralPath (Join-Path $state 'ready.json') -Force -ErrorAction SilentlyContinue;Start-Service $serviceName;WaitReady $image
        AtomicText $highestPath $manifest.Version;Atomic $transaction @{Status='committed';Previous=$previous;Candidate=$image}
    }catch{Rollback @{Previous=$previous;Candidate=$image};throw}
}finally{if($idle){$idle.Dispose()};$lock.Dispose()}
