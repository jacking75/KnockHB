# Function-only contract check. Does not run installer code, SCM, UAC or any filesystem mutation.
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repository 'deploy/install.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Installer parse failure'}
$function=$ast.Find({param($node) $node-is[Management.Automation.Language.FunctionDefinitionAst]-and$node.Name-eq'ServiceImage'},$true)
if(!$function){throw 'Installer ownership boundary missing'}
Invoke-Expression $function.Extent.Text
$root='C:\Program Files\RemoteAssist'
$valid='C:\Program Files\RemoteAssist\versions\1.2.3.4\RemoteAssist.Host.Service.exe'
if((ServiceImage ('"'+$valid+'"'))-ne$valid){throw 'Owned version rejected'}
foreach($bad in @($valid,('"'+$valid+'" --extra'),('"'+$root+'\versions-foreign\1.2.3.4\RemoteAssist.Host.Service.exe"'),('"'+$root+'\versions\..\foreign\RemoteAssist.Host.Service.exe"'),('"'+$root+'\versions\RemoteAssist.Host.Service.exe"'),('"'+$root+'\versions\1.2.3.4\foreign.exe"'))){
    $rejected=$false;try{$null=ServiceImage $bad}catch{$rejected=$true};if(!$rejected){throw ('Unsafe service image accepted: '+$bad)}
}
Write-Output 'PASS installer service ownership rejects unquoted/foreign/traversal/argument images before stop'
