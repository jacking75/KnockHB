param([ValidateSet('Install','Recover','Remove','Support')][string]$Action='Install',[Parameter(Mandatory=$true)][string]$Package)
$ErrorActionPreference='Stop'
$root=Join-Path $env:ProgramFiles 'RemoteAssist'
$state=Join-Path $env:ProgramData 'RemoteAssist'
$utf8=New-Object Text.UTF8Encoding($false)
$serviceName='RemoteAssistHost'
function AtomicText($path,$text){$temp=$path+'.'+[guid]::NewGuid().ToString('N');try{$bytes=$utf8.GetBytes($text);$stream=New-Object IO.FileStream($temp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,4096,[IO.FileOptions]::WriteThrough);try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()};if(Test-Path -LiteralPath $path){[IO.File]::Replace($temp,$path,[NullString]::Value)}else{[IO.File]::Move($temp,$path)}}finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Force}}}
function Atomic($path,$value){AtomicText $path ($value|ConvertTo-Json -Depth 8)}
function Native($name,$arguments){& $name @arguments | Out-Null;if($LASTEXITCODE -ne 0){throw ('Native command failed: '+$name)}}
function Protect($path,$readUsers){$grants=@('*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F');if($readUsers){$grants+= '*S-1-5-32-545:(OI)(CI)RX'};Native "$env:SystemRoot\System32\icacls.exe" (@($path,'/inheritance:r','/grant:r')+$grants);Native "$env:SystemRoot\System32\icacls.exe" @($path,'/setowner','*S-1-5-32-544','/T')}
function SafeTree($path){$full=[IO.Path]::GetFullPath($path);if(!(Test-Path -LiteralPath $full)){return};for($p=Get-Item -LiteralPath $full;$p;$p=$p.Parent){if($p.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse installation path'}};foreach($item in Get-ChildItem -LiteralPath $full -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse installation item'}}}
function ServiceImage([string]$Path){
    if(!$Path-or$Path.Length-lt3-or!$Path.StartsWith('"')-or!$Path.EndsWith('"')){throw 'Quoted owned service image required'}
    $image=$Path.Substring(1,$Path.Length-2);$full=[IO.Path]::GetFullPath($image)
    $prefix=[IO.Path]::GetFullPath((Join-Path $root 'versions'))+[IO.Path]::DirectorySeparatorChar
    if($full-ne$image-or!$full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or[IO.Path]::GetFileName($full)-ne'RemoteAssist.Host.Service.exe'-or$full.Substring($prefix.Length).Split('\').Count-ne2){throw 'Service name belongs to another installation'}
    return $full
}
function OwnedService(){
    $owned=Get-CimInstance Win32_Service -Filter "Name='RemoteAssistHost'" -OperationTimeoutSec 5
    if($owned){$null=ServiceImage $owned.PathName;SafeTree ([IO.Path]::GetDirectoryName((ServiceImage $owned.PathName)))}
    return $owned
}
function StopOwned(){if(OwnedService){Stop-Service -Name $serviceName -ErrorAction Stop;(Get-Service $serviceName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(15))}}
function SetImage($image){$image=ServiceImage ('"'+$image+'"');Native "$env:SystemRoot\System32\sc.exe" @('config',$serviceName,'binPath=',('"'+$image+'"'),'start=','auto')}
function SupportShortcutPath(){
    $directory=[Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
    if(!$directory-or!(Test-Path -LiteralPath $directory -PathType Container)){throw 'Common Start menu directory missing'}
    for($item=Get-Item -LiteralPath $directory;$item;$item=$item.Parent){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Reparse Start menu path'}}
    $path=Join-Path $directory 'RemoteAssist Support.lnk'
    if(Test-Path -LiteralPath $path){SafeTree $path;if((Get-Item -LiteralPath $path).PSIsContainer){throw 'Start menu entry is a directory'}}
    return $path
}
function OwnedSupportShortcut($shortcut){
    if($shortcut.Description-ne'RemoteAssist.Support.v1'-or$shortcut.Arguments-ne'support'){return $false}
    if([IO.Path]::GetFileName($shortcut.TargetPath)-ne'RemoteAssist.Update.exe'){return $false}
    try{$directory=Split-Path $shortcut.TargetPath -Parent;$null=ServiceImage ('"'+(Join-Path $directory 'RemoteAssist.Host.Service.exe')+'"');return $true}catch{return $false}
}
function SupportShortcut([string]$Image,[switch]$Remove){
    $path=SupportShortcutPath;$shell=New-Object -ComObject WScript.Shell;$shortcut=$null;$fresh=$null;$temporary=$null
    try{
        if(Test-Path -LiteralPath $path){
            $shortcut=$shell.CreateShortcut($path)
            if(!(OwnedSupportShortcut $shortcut)){if($Remove){return};throw 'Start menu shortcut belongs to another application'}
        }
        if($Remove){if($shortcut){Remove-Item -LiteralPath $path -Force};return}
        $image=ServiceImage ('"'+$Image+'"');$directory=Split-Path $image -Parent;SafeTree $directory
        $target=Join-Path $directory 'RemoteAssist.Update.exe';if(!(Test-Path -LiteralPath $target -PathType Leaf)){throw 'Support launcher missing'}
        $temporary=Join-Path (Split-Path $path -Parent) ('RemoteAssist-'+[guid]::NewGuid().ToString('N')+'.lnk')
        $fresh=$shell.CreateShortcut($temporary);$fresh.TargetPath=$target;$fresh.Arguments='support';$fresh.WorkingDirectory=$directory;$fresh.Description='RemoteAssist.Support.v1';$fresh.IconLocation=$target+',0';$fresh.Save()
        if(Test-Path -LiteralPath $path){[IO.File]::Replace($temporary,$path,[NullString]::Value)}else{[IO.File]::Move($temporary,$path)}
    }finally{
        if($temporary-and(Test-Path -LiteralPath $temporary)){Remove-Item -LiteralPath $temporary -Force}
        foreach($object in @($fresh,$shortcut,$shell)){if($object){$null=[Runtime.InteropServices.Marshal]::FinalReleaseComObject($object)}}
    }
}
function WaitReady($image){$clock=[Diagnostics.Stopwatch]::StartNew();while($clock.Elapsed.TotalSeconds-lt45){if(Test-Path -LiteralPath (Join-Path $state 'ready.json')){$ready=Get-Content -LiteralPath (Join-Path $state 'ready.json') -Encoding UTF8|ConvertFrom-Json;$process=Get-Process -Id $ready.Pid -ErrorAction SilentlyContinue;if($process-and$process.Path-eq$image-and$ready.Image-eq$image){return}};$service=Get-Service $serviceName;if($service.Status-eq'Stopped'){throw 'New service stopped before readiness'};Start-Sleep -Milliseconds 250};throw 'New service readiness timeout'}
function PrepareHost([string]$Directory,[string]$Mode){
    SafeTree $Directory
    $script=Join-Path $Directory 'prepare-host.ps1'
    if(!(Test-Path -LiteralPath $script)){throw 'Startup preparation script missing'}
    # Verified protected package script; process-only policy avoids publisher prompts.
    # MachinePolicy/UserPolicy remain authoritative and may deny execution.
    $command='-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$script+'" -Action '+$Mode+' -InstallationRoot "'+$Directory+'"'
    $process=Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -ArgumentList $command -WindowStyle Hidden -PassThru
    try{$null=$process.Handle;if(!$process.WaitForExit(30000)){$process.Kill();$null=$process.WaitForExit(2000);throw 'Startup preparation exceeded 30 seconds'};if($process.ExitCode-ne0){throw ('Startup preparation failed: '+$Mode)}}finally{$process.Dispose()}
}
function Rollback($tx){StopOwned;if($tx.Candidate){$candidateImage=ServiceImage ('"'+$tx.Candidate+'"');$candidateRoot=Split-Path $candidateImage -Parent;if(Test-Path (Join-Path $candidateRoot 'prepare-host.ps1')){PrepareHost $candidateRoot 'Rollback'}};if($tx.Previous){if(!$tx.Previous.StartsWith($root+'\versions\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe rollback image'};SetImage $tx.Previous;Remove-Item -LiteralPath (Join-Path $state 'ready.json') -Force -ErrorAction SilentlyContinue;Start-Service $serviceName;WaitReady $tx.Previous}elseif(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){Native "$env:SystemRoot\System32\sc.exe" @('delete',$serviceName)};Atomic (Join-Path $root 'transaction.json') @{Status='rolled-back';Previous=$tx.Previous;Candidate=$tx.Candidate}}
$admin=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent());if(!$admin.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required'}
SafeTree $root;SafeTree $state
if(!(Test-Path -LiteralPath $root)){New-Item -ItemType Directory -Path $root|Out-Null;Protect $root $true}
if(!(Test-Path -LiteralPath $state)){New-Item -ItemType Directory -Path $state|Out-Null;Protect $state $false}
$lock=New-Object IO.FileStream((Join-Path $root 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
    $idle=$null
    if($Action-in@('Recover','Remove')){$idle=New-Object IO.FileStream((Join-Path $state 'session.gate'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::Read)}
    $transaction=Join-Path $root 'transaction.json'
    if(Test-Path -LiteralPath $transaction){$pending=Get-Content -LiteralPath $transaction -Encoding UTF8|ConvertFrom-Json;if($pending.Status-eq'pending'){Rollback $pending}elseif($pending.Status-eq'committed'){$committedImage=ServiceImage ('"'+$pending.Candidate+'"');$committedRoot=Split-Path $committedImage -Parent;if(Test-Path (Join-Path $committedRoot 'prepare-host.ps1')){PrepareHost $committedRoot 'Commit'};if($Action-ne'Remove'){SupportShortcut $committedImage}}}
    if($Action-eq'Recover'){exit 0}
    if($Action-eq'Support'){
        $owned=OwnedService;if(!$owned){throw 'Installed Host service required'}
        $image=ServiceImage $owned.PathName;$directory=Split-Path $image -Parent
        if([IO.Path]::GetFullPath($Package).TrimEnd('\')-ne$directory){throw 'Support launcher must belong to active installation'}
        # The elevated launcher wrote the support grant. The service re-reads it after every restart.
        $ticket=Join-Path $state 'initial-support.json';if(!(Test-Path -LiteralPath $ticket)){throw 'Interactive launch authorization required'}
        StopOwned
        try{PrepareHost $directory 'Rollback';PrepareHost $directory 'Prepare';PrepareHost $directory 'Verify';Remove-Item -LiteralPath (Join-Path $state 'ready.json') -Force -ErrorAction SilentlyContinue;Start-Service $serviceName;WaitReady $image;PrepareHost $directory 'Commit'}
        catch{StopOwned;PrepareHost $directory 'Rollback';throw}
        exit 0
    }
    if($Action-eq'Remove'){
        SupportShortcut -Remove
        Remove-Item -LiteralPath (Join-Path $state 'initial-support.json') -Force -ErrorAction SilentlyContinue
        StopOwned;if(Get-Service -Name $serviceName -ErrorAction SilentlyContinue){Native "$env:SystemRoot\System32\sc.exe" @('delete',$serviceName)};foreach($name in @('RemoteAssist.Update','RemoteAssist.Recover')){Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue};$versions=Join-Path $root 'versions';SafeTree $versions;if(Test-Path -LiteralPath $versions){foreach($version in Get-ChildItem -LiteralPath $versions -Directory){if(Test-Path (Join-Path $version.FullName 'prepare-host.ps1')){PrepareHost $version.FullName 'Remove'}};Remove-Item -LiteralPath $versions -Recurse -Force}
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
    $image=Join-Path $candidate 'RemoteAssist.Host.Service.exe';$existing=OwnedService; $previous=$null
    if($existing){$previous=ServiceImage $existing.PathName}
    StopOwned
    foreach($file in @('RemoteAssist.Update.exe','install.ps1')){if(!(Test-Path -LiteralPath (Join-Path $root $file))){Copy-Item -LiteralPath (Join-Path $candidate $file) -Destination (Join-Path $root $file)}}
    if(!(Test-Path -LiteralPath (Join-Path $root 'trusted-public.pem'))){Copy-Item -LiteralPath (Join-Path $candidate 'trusted-public.pem') -Destination $root}
    $worker=Join-Path $root 'RemoteAssist.Update.exe';$principal=New-ScheduledTaskPrincipal -UserId SYSTEM -LogonType ServiceAccount -RunLevel Highest
    foreach($task in @(@{Name='RemoteAssist.Recover';Command='recover';Trigger=(New-ScheduledTaskTrigger -AtStartup)},@{Name='RemoteAssist.Update';Command='update';Trigger=(New-ScheduledTaskTrigger -Daily -At '03:00')})){Register-ScheduledTask -TaskName $task.Name -Action (New-ScheduledTaskAction -Execute $worker -Argument $task.Command) -Trigger $task.Trigger -Principal $principal -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::FromMinutes(15))) -Force|Out-Null}
    Atomic $transaction @{Status='pending';Previous=$previous;Candidate=$image}
    try{
        PrepareHost $candidate 'Prepare'
        if($existing){SetImage $image}else{Native "$env:SystemRoot\System32\sc.exe" @('create',$serviceName,'binPath=',('"'+$image+'"'),'start=','auto','obj=','LocalSystem')}
        Remove-Item -LiteralPath (Join-Path $state 'ready.json') -Force -ErrorAction SilentlyContinue;Start-Service $serviceName;WaitReady $image
        AtomicText $highestPath $manifest.Version;Atomic $transaction @{Status='committed';Previous=$previous;Candidate=$image}
    }catch{Rollback @{Previous=$previous;Candidate=$image};throw}
    # The durable install commit precedes journal cleanup; recovery finishes interrupted cleanup.
    PrepareHost $candidate 'Commit'
    SupportShortcut $image
}finally{if($idle){$idle.Dispose()};$lock.Dispose()}
