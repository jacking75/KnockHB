$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
if(Test-Path (Join-Path $owner.root 'product-start.json')){throw 'Started campaign requires normal recovery'}
foreach($name in @($owner.task,$owner.watchdog)){
 $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
 if(!$task){continue}
 if($task.State-eq'Running'-or$task.Principal.UserId-notin@('SYSTEM','S-1-5-18')-or$task.Actions.Count-ne1-or
    $task.Actions[0].Arguments-notlike('*"'+$owner.root+'"*')){throw 'Prepared task ownership/state changed'}
 Unregister-ScheduledTask -TaskName $name -Confirm:$false
}
@{status='completed';id=$owner.id;preparedTasksRetired=$true;productStarted=$false;filesRetainedForReview=$true}|ConvertTo-Json -Compress
