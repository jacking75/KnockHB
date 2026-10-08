param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference='Stop'
$pin=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/source-recipes.json') -Encoding utf8|ConvertFrom-Json
$target=[IO.Path]::GetFullPath($Destination);if(Test-Path -LiteralPath $target){throw 'Use a new source-bundle directory'}
New-Item -ItemType Directory -Path $target|Out-Null
$recipe=Join-Path $target 'build-recipes.tar.gz';Invoke-WebRequest -Uri $pin.recipeArchiveUrl -OutFile $recipe -UseBasicParsing
if((Get-FileHash -LiteralPath $recipe).Hash.ToLowerInvariant()-ne$pin.recipeArchiveSha256){throw 'Recipe archive hash mismatch'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/notices') -Destination $target -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/source-recipes.json') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/supply.json') -Destination $target
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/libraries.json') -Destination $target
$env:GIT_TERMINAL_PROMPT='0'
$records=@();$seen=@{};Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach($source in $pin.sources){
    if($source.repo-notmatch'^https://[A-Za-z0-9.-]+/[A-Za-z0-9_./-]+$'-or$source.ref-notmatch'^[0-9a-f]{40}$'){throw 'Source URL/commit must be immutable and HTTPS'}
    $identity=$source.repo+'@'+$source.ref;if($seen.ContainsKey($identity)){continue};$seen[$identity]=$true
    $name=[IO.Path]::GetFileNameWithoutExtension($source.repo)+'-'+$source.ref
    $directory=Join-Path $target $name;New-Item -ItemType Directory -Path $directory|Out-Null
    & git -C $directory init --quiet;if($LASTEXITCODE-ne0){throw 'Source init failed'}
    & git -C $directory -c protocol.file.allow=never fetch --quiet --depth 1 $source.repo $source.ref;if($LASTEXITCODE-ne0){throw ('Source fetch failed: '+$source.recipe)}
    & git -C $directory checkout --quiet --detach FETCH_HEAD;if($LASTEXITCODE-ne0){throw 'Source checkout failed'}
    $actual=(& git -C $directory rev-parse HEAD).Trim();if($actual-ne$source.ref){throw 'Source commit mismatch'}
    & git -C $directory -c protocol.file.allow=never -c protocol.ext.allow=never submodule update --init --recursive --depth 1;if($LASTEXITCODE-ne0){throw 'Source submodule preparation failed'}
    $submodules=@(& git -C $directory submodule status --recursive)
    $licenses=@(Get-ChildItem -LiteralPath $directory -Recurse -File|Where-Object {$_.Name-match'^(COPYING|LICENSE|NOTICE|COPYRIGHT)([._-].*)?$'}|ForEach-Object {$_.FullName.Substring($directory.Length+1)})
    if(!$licenses.Count){throw ('License inventory is missing: '+$name)}
    $records+=@{name=$name;repo=$source.repo;commit=$actual;recipe=$source.recipe;submodules=$submodules;licenseFiles=$licenses}
    Write-Output ('Prepared source/license inventory: '+$name)
}
$utf8=New-Object Text.UTF8Encoding($false)
$supply=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/supply.json') -Encoding utf8|ConvertFrom-Json
[IO.File]::WriteAllText((Join-Path $target 'inventory.json'),(@{recipeCommit=$pin.recipeCommit;archiveSha256=$supply.sha256;sources=$records;preparedUtc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 8),$utf8)
# Keep git histories, submodules, patches and notices with the corresponding sources.
[IO.Compression.ZipFile]::CreateFromDirectory($target,$target+'.zip',[IO.Compression.CompressionLevel]::Optimal,$false)
Write-Output ('Source bundle SHA256: '+(Get-FileHash -LiteralPath ($target+'.zip')).Hash.ToLowerInvariant())
