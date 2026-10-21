$ErrorActionPreference='Stop'
$owner=Get-E2eOwner
$result=@{}
foreach($name in @('service-status.ndjson','product-error.json','product-cleanup.json')){
 $path=Join-Path $owner.root $name
 if((Test-Path $path)-and(Get-Item $path).Length-lt524288){$result[$name]=[Convert]::ToBase64String([IO.File]::ReadAllBytes($path))}
}
if($CampaignManifest.version-eq4){
 $inventory=@(Get-CimInstance Win32_Process -OperationTimeoutSec 3|Where-Object {$_.ExecutablePath-like "$env:ProgramFiles\RemoteAssist-Dev\*"}|ForEach-Object {
  @{pid=$_.ProcessId;image=$_.ExecutablePath;session=$_.SessionId;createdAt=$_.CreationDate.ToUniversalTime().ToString('o')}
 })
 $metadata=@{id=$owner.id;processes=$inventory;tick=[Diagnostics.Stopwatch]::GetTimestamp();frequency=[Diagnostics.Stopwatch]::Frequency}
 $receive=Join-Path $owner.output 'receive'
 $metadata.receive=@(Get-ChildItem -LiteralPath $receive -File|ForEach-Object {
  $null=Assert-E2ePath $_.FullName
  if($_.Length-gt1048576){throw 'Fixture receive file size'}
  @{name=$_.Name;size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
 })
 $result['product-inventory.json']=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($metadata|ConvertTo-Json -Depth 5)))
 $book=Join-Path $env:ProgramFiles 'RemoteAssist-Dev/runbooks/network-summary.ps1'
 if(Test-Path -LiteralPath $book){
  $null=Assert-E2ePath $book
  $content=[IO.File]::ReadAllText($book).Replace("`r`n","`n")
  $sha=[Security.Cryptography.SHA256]::Create()
  try{$hash=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($content)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
  $result['installed-book.json']=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes((@{hash=$hash}|ConvertTo-Json)))
 }
 $data=Join-Path $env:ProgramData 'RemoteAssist'
 $marker=Join-Path $data 'dev-owner.json'
 if(Test-Path -LiteralPath $marker){
  $installed=Read-E2eJson $marker
  if($installed.id-ne$owner.id){throw 'Audit owner changed'}
  $audit=Join-Path $data 'audit/audit.ndjson'
  if(Test-Path -LiteralPath $audit){
   $null=Assert-E2ePath $audit
   if((Get-Item -LiteralPath $audit).Length-gt524288){throw 'Audit evidence bound'}
   $result['audit.ndjson']=[Convert]::ToBase64String([IO.File]::ReadAllBytes($audit))
  }
 }
 $live=Join-Path $env:ProgramFiles 'RemoteAssist-Dev/service-status.ndjson'
 if(Test-Path -LiteralPath $live){
  $installed=Read-E2eJson (Join-Path (Split-Path $live) 'dev-owner.json')
  if($installed.id-ne$owner.id){throw 'Service evidence owner changed'}
  if((Get-Item -LiteralPath $live).Length-gt524288){throw 'Service evidence bound'}
  $result['service-status.ndjson']=[Convert]::ToBase64String([IO.File]::ReadAllBytes($live))
 }
 $trace=Join-Path $owner.root 'trace'
 foreach($file in Get-ChildItem -LiteralPath $trace -File){
  if($file.Name-notmatch'^trace-[0-9]+-[0-9]+\.ndjson$'-or$file.Length-gt1048576){throw 'Trace evidence contract'}
  $null=Assert-E2ePath $file.FullName
  $stream=New-Object IO.FileStream($file.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
  try{$memory=New-Object IO.MemoryStream;$stream.CopyTo($memory);$result[$file.Name]=[Convert]::ToBase64String($memory.ToArray());$memory.Dispose()}finally{$stream.Dispose()}
 }
}
$result|ConvertTo-Json
