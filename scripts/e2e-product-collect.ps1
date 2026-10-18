$ErrorActionPreference='Stop'
$owner=Get-Content (Join-Path $Staging 'product-owner.json') -Raw -Encoding UTF8|ConvertFrom-Json
if($owner.id-ne'842dd89df2104c0ba7a2aa99cc9fb3e0'-or$owner.root-ne(Join-Path $env:ProgramData ('RemoteAssist-E2E-Product-'+$owner.id))){throw 'Frozen campaign mismatch'}
$root=$owner.root
$started=Test-Path (Join-Path $root 'product-start.json')
if($started-and!(Test-Path (Join-Path $root 'product-cleanup.json'))){throw 'Product cleanup not yet recorded; preserve watchdog task'}
Add-Type -Path (Join-Path $root 'scripts/e2e-native.cs')
$deadline=[E2eOwnedCommand]::Deadline(30000)
$tasks=@($owner.observer,$owner.task)
foreach($name in $tasks){
 $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
 if(!$task){continue}
 if($task.Actions.Arguments-notlike('*'+$root+'*')){throw 'Task ownership changed'}
 foreach($args in @(@('/End','/TN',$name),@('/Delete','/TN',$name,'/F'))){
  $cmd=[E2eOwnedCommand]::Start((Join-Path $env:SystemRoot 'System32/schtasks.exe'),[string[]]$args)
  try{$exit=$cmd.Wait($deadline,10000)}finally{$cmd.Dispose()}
  if($args[0]-eq'/Delete'-and$exit-ne0){throw 'Owned task deletion failed'}
 }
}
$service=Get-Service RemoteAssistHost -ErrorAction SilentlyContinue
$remaining=@(Get-Process -Name 'RemoteAssist.Host.*' -ErrorAction SilentlyContinue|Where-Object {$_.Path-and$_.Path.StartsWith((Join-Path $env:ProgramFiles 'RemoteAssist-Dev\'),[StringComparison]::OrdinalIgnoreCase)}|Select-Object Id,ProcessName)
@{serviceAbsent=!$service;ownedProcesses=$remaining;tasksAbsent=!@(Get-ScheduledTask -TaskName $tasks -ErrorAction SilentlyContinue).Count;productStarted=$started;protectedEvidence=$root}|ConvertTo-Json -Depth 4
