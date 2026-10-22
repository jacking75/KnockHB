$ErrorActionPreference='Stop'

# Extract only pure functions. Never dot-source the installer: its top-level code
# mutates services, scheduled tasks and installation directories.
$installer=Join-Path $PSScriptRoot '../deploy/install.ps1'
$tokens=$null;$errors=$null
$tree=[Management.Automation.Language.Parser]::ParseFile([IO.Path]::GetFullPath($installer),[ref]$tokens,[ref]$errors)
if($errors.Count){throw ($errors|Out-String)}
$root=Join-Path $env:ProgramFiles 'RemoteAssist'
foreach($name in @('ServiceImage','OwnedSupportShortcut')){
    $node=$tree.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name-eq$name},$true)
    if(!$node){throw ('Missing function: '+$name)}
    Invoke-Expression $node.Extent.Text
}
function Assert($condition,$message){if(!$condition){throw $message}}
function RejectImage($value){try{$null=ServiceImage $value}catch{return};throw ('Invalid service image accepted: '+$value)}

$target=Join-Path $root 'versions\1.2.3.4\RemoteAssist.Update.exe'
$owned=[pscustomobject]@{Description='RemoteAssist.Support.v1';Arguments='support';TargetPath=$target}
Assert (OwnedSupportShortcut $owned) 'Owned shortcut rejected'
$owned.Arguments='remove';Assert (!(OwnedSupportShortcut $owned)) 'Foreign shortcut arguments accepted'
$owned.Arguments='support';$owned.Description='Other application';Assert (!(OwnedSupportShortcut $owned)) 'Foreign shortcut marker accepted'
$owned.Description='RemoteAssist.Support.v1'
foreach($foreign in @(
    (Join-Path $root 'versions\..\RemoteAssist.Update.exe'),
    (Join-Path $root 'versions\1.2.3.4\nested\RemoteAssist.Update.exe'),
    (Join-Path $root 'RemoteAssist.Update.exe'),
    (Join-Path $root 'versions\1.2.3.4\Other.exe'),
    (Join-Path ($root+'-foreign') 'versions\1.2.3.4\RemoteAssist.Update.exe')
)){$owned.TargetPath=$foreign;Assert (!(OwnedSupportShortcut $owned)) ('Foreign shortcut target accepted: '+$foreign)}
$owned.TargetPath=Join-Path $root 'versions\1.2.3.4-repair-0123456789abcdef0123456789abcdef\RemoteAssist.Update.exe'
Assert (OwnedSupportShortcut $owned) 'Owned repair-version shortcut rejected'

$image=Join-Path $root 'versions\1.2.3.4\RemoteAssist.Host.Service.exe'
Assert ((ServiceImage ('"'+$image+'"'))-eq$image) 'Valid service image rejected'
RejectImage $image
RejectImage ('"'+$image+'" --foreign-argument')
RejectImage ('"'+(Join-Path $root 'versions\..\RemoteAssist.Host.Service.exe')+'"')

'PASS support installer: syntax, owned/foreign shortcut identity, canonical service path; no COM or native calls'
