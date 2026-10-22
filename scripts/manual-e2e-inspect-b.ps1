$ErrorActionPreference='Stop'
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
$admin=([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$service=Get-CimInstance Win32_Service -Filter "Name='RemoteAssistHost'"
$processes=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -match '^(Agent|RemoteAssist\..*)\.exe$'} | Select-Object ProcessId,Name,SessionId,ExecutablePath)
$policy=Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
@{utc=[DateTime]::UtcNow.ToString('o');sshAdmin=$admin;user=$identity.Name;service=($service|Select-Object Name,State,PathName);processes=$processes;installPresent=(Test-Path 'C:\Program Files\RemoteAssist-Dev');dataPresent=(Test-Path 'C:\ProgramData\RemoteAssist');uac=@{EnableLUA=$policy.EnableLUA;ConsentPromptBehaviorAdmin=$policy.ConsentPromptBehaviorAdmin;PromptOnSecureDesktop=$policy.PromptOnSecureDesktop};console=(query user | Out-String)} | ConvertTo-Json -Depth 5
