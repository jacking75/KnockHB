param([Parameter(Mandatory)][string]$Destination,[string]$LicenseDirectory)
$ErrorActionPreference='Stop';$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$target=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $target){throw 'New notices output directory required'};New-Item -ItemType Directory -Path $target|Out-Null
$cache=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget/packages'};$packages=@{}
foreach($lock in Get-ChildItem -LiteralPath (Join-Path $repo 'src') -Filter packages.lock.json -Recurse){$data=Get-Content -LiteralPath $lock.FullName -Raw -Encoding UTF8|ConvertFrom-Json;foreach($framework in $data.dependencies.PSObject.Properties){foreach($item in $framework.Value.PSObject.Properties){if($item.Value.type-ne'Project'){$packages[$item.Name.ToLowerInvariant()+'/'+$item.Value.resolved]=$item.Value.contentHash}}}}
Add-Type -AssemblyName System.IO.Compression.FileSystem;$records=@();$missing=@();$utf8=New-Object Text.UTF8Encoding($false)
foreach($identity in $packages.Keys|Sort-Object){
    $parts=$identity.Split('/');$directory=Join-Path $cache $identity;$nupkg=Join-Path $directory ($parts[0]+'.'+$parts[1]+'.nupkg');if(!(Test-Path -LiteralPath $nupkg)){throw ('Restore pinned package first: '+$identity)}
    $hash=Get-Content -LiteralPath ($nupkg+'.sha512') -Raw;if($hash.Trim()-ne$packages[$identity]){throw ('Package content hash mismatch: '+$identity)}
    $archive=[IO.Compression.ZipFile]::OpenRead($nupkg);$copied=@();$expression='';$project=''
    try{
        $nuspec=@($archive.Entries|Where-Object FullName -like '*.nuspec');if($nuspec.Count-ne1){throw 'Package metadata missing'};$reader=New-Object IO.StreamReader($nuspec[0].Open());try{[xml]$meta=$reader.ReadToEnd()}finally{$reader.Dispose()};$license=$meta.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="license"]');$projectNode=$meta.SelectSingleNode('//*[local-name()="metadata"]/*[local-name()="projectUrl"]');if($projectNode){$project=$projectNode.InnerText};if($license){$expression=$license.InnerText}
        $out=Join-Path $target ($parts[0]+'-'+$parts[1]);New-Item -ItemType Directory -Path $out|Out-Null
        foreach($entry in $archive.Entries|Where-Object {([IO.Path]::GetFileName($_.FullName)-match'^(LICENSE|LICENCE|COPYING|NOTICE|COPYRIGHT|ThirdPartyNotices)([._-].*)?$')-or($license-and$license.type-eq'file'-and$_.FullName-eq$expression)}){
            if($entry.Length-gt1048576){throw 'License text size limit'};$name=($entry.FullName-replace'[^A-Za-z0-9_.-]','_');$file=Join-Path $out $name;$input=$entry.Open();$output=[IO.File]::Create($file);try{$input.CopyTo($output)}finally{$input.Dispose();$output.Dispose()};$copied+=($out.Substring($target.Length+1)+'/'+$name)
        }
        if(!$copied.Count-and$license-and$license.type-eq'expression'-and$expression-match'^[A-Za-z0-9.-]+$'-and$LicenseDirectory){$full=Join-Path $LicenseDirectory ($expression+'.txt');if(Test-Path -LiteralPath $full){Copy-Item -LiteralPath $full -Destination (Join-Path $out 'LICENSE.txt');$copied+=($out.Substring($target.Length+1)+'/LICENSE.txt')}}
        if(!$copied.Count){$missing+=$identity}
        $records+=@{package=$identity;contentHash=$packages[$identity];license=$expression;project=$project;fullTexts=$copied}
    }finally{$archive.Dispose()}
}
# Runtime distributions carry their own notices; never replace these with a package count.
$dotnet=(Get-Command dotnet).Source;$runtimeNotices=@(Get-ChildItem -LiteralPath (Join-Path (Split-Path $dotnet) 'packs') -Recurse -File|Where-Object {$_.Name-match'^(LICENSE|ThirdPartyNotices)\.(txt|TXT)$'})
foreach($file in $runtimeNotices){$name=($file.FullName.Substring((Split-Path $dotnet).Length+1)-replace'[^A-Za-z0-9_.-]','_');Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $target $name)}
[IO.File]::WriteAllText((Join-Path $target 'managed-inventory.json'),(@{packages=$records;runtimeTexts=$runtimeNotices.Count;missing=$missing;preparedUtc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 8),$utf8)
if($missing.Count-or!$runtimeNotices.Count){throw 'Full package/runtime license texts missing. Supply reviewed SPDX texts or corresponding package sources; see inventory.'}
Write-Output $target
