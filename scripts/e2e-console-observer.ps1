param([Parameter(Mandatory=$true)][string]$RunDir,[Parameter(Mandatory=$true)][string]$OutputDir)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -Path (Join-Path $PSScriptRoot 'e2e-native.cs')
$start=Get-Content (Join-Path $RunDir 'product-start.json') -Raw -Encoding UTF8|ConvertFrom-Json
$deadline=[long]$start.activeEnd
$expected=Join-Path $env:ProgramFiles 'RemoteAssist-Dev/RemoteAssist.Host.Tray.exe'
$utf8=New-Object Text.UTF8Encoding($false)
$invoked=$false;$paired=$false
function Write-Proof($Name,$Value){
 $path=Join-Path $OutputDir $Name;$temp=$path+'.tmp';[IO.File]::WriteAllText($temp,($Value|ConvertTo-Json -Depth 4),$utf8)
 if(Test-Path $path){[IO.File]::Replace($temp,$path,[NullString]::Value)}else{[IO.File]::Move($temp,$path)}
}
try{
 while([E2eOwnedCommand]::Remaining($deadline)-gt0-and!(Test-Path (Join-Path $RunDir 'product-cleanup.json'))){
  $process=@(Get-Process -Name RemoteAssist.Host.Tray -ErrorAction SilentlyContinue|Where-Object {$_.Path-eq$expected-and$_.SessionId-eq[Diagnostics.Process]::GetCurrentProcess().SessionId})
  if($process.Count-eq1){
   $condition=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty,$process[0].Id)
   $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition)
   $controls=@{}
   foreach($id in @('HostStart','HostStatus','PairingCode','PairingPin','ConsentApprove','HostStop')){
    $match=New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    foreach($window in $windows){$found=$window.FindFirst([Windows.Automation.TreeScope]::Descendants,$match);if($found){$controls[$id]=$found;break}}
   }
   if(!$invoked-and$controls.HostStart-and$controls.HostStart.Current.IsEnabled){
    $controls.HostStart.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke();$invoked=$true
   }
   if(!$paired-and$controls.PairingCode-and$controls.PairingPin){
    $code=$controls.PairingCode.Current.Name;$pin=$controls.PairingPin.Current.Name.Replace('PIN ','')
    if($code-match'^\d{3}-?\d{3}-?\d{3}$'-and$pin-match'^\d{4}$'){
     Write-Proof 'pairing.json' @{code=$code;pin=$pin};$paired=$true
    }
   }
   Write-Proof 'ui.json' @{tick=[E2eOwnedCommand]::Tick;pid=$process[0].Id;session=$process[0].SessionId;started=$invoked;paired=$paired;consentVisible=[bool]$controls.ConsentApprove;status=$(if($controls.HostStatus){$controls.HostStatus.Current.Name}else{''})}
  }
  Start-Sleep -Milliseconds 200
 }
}catch{Write-Proof 'observer-error.json' @{error=$_.Exception.Message;line=$_.InvocationInfo.ScriptLineNumber}}
