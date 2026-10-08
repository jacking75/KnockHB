param([Parameter(Mandatory=$true)][string]$Version,[Parameter(Mandatory=$true)][string]$Settings,[Parameter(Mandatory=$true)][string]$CertificateThumbprint,[Parameter(Mandatory=$true)][string]$TimestampUrl,[Parameter(Mandatory=$true)][string]$SigningKeyPem,[Parameter(Mandatory=$true)][string]$SourceBundle,[Parameter(Mandatory=$true)][uri]$SourceUrl,[Parameter(Mandatory=$true)][string]$ManagedNotices,[string]$Output=(Join-Path $PSScriptRoot '../.artifacts/release'))
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major-lt7){throw 'Packaging requires PowerShell 7 with RSA PEM/PSS support'}
$parsedVersion=$null
if($Version-notmatch'^(0|[1-9]\d{0,4})(\.(0|[1-9]\d{0,4})){3}$'-or!([version]::TryParse($Version,[ref]$parsedVersion))){throw 'Four-part numeric version required'}
if(([uri]$TimestampUrl).Scheme-ne'https'){throw 'HTTPS timestamp endpoint required'}
$certificate=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$CertificateThumbprint)
if(!$certificate.HasPrivateKey-or$certificate.NotAfter-lt(Get-Date)){throw 'Usable signing certificate required'}
$dest=[IO.Path]::GetFullPath($Output);if(Test-Path -LiteralPath $dest){throw 'Use a new release output directory'}
$stage=Join-Path $dest 'payload';New-Item -ItemType Directory -Path $stage -Force|Out-Null
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
foreach($name in @('Service','Tray','Helper','AdminHelper','Recovery','RunbookWorker')){
    $project=Join-Path $repo "src/RemoteAssist.Host.$name/RemoteAssist.Host.$name.csproj"
    & dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $stage
    if($LASTEXITCODE-ne0){throw "Publish failed: $name"}
}
$update=Join-Path $dest 'update'; & dotnet publish (Join-Path $repo 'src/RemoteAssist.Update/RemoteAssist.Update.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:Version=$Version -o $update
if($LASTEXITCODE-ne0){throw 'Updater publish failed'}
Copy-Item -LiteralPath (Join-Path $update 'RemoteAssist.Update.exe') -Destination $stage
$native=Join-Path $repo 'third_party/ffmpeg/bin';if(!(Test-Path -LiteralPath $native)){throw 'Prepare verified FFmpeg first'}
New-Item -ItemType Directory -Path (Join-Path $stage 'ffmpeg')|Out-Null
Get-ChildItem -LiteralPath $native -Filter '*.dll'|ForEach-Object {Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $stage 'ffmpeg')}
Copy-Item -LiteralPath (Join-Path $repo 'third_party/ffmpeg/notices') -Destination $stage -Recurse
$managed=Get-Content -LiteralPath (Join-Path $ManagedNotices 'managed-inventory.json') -Raw -Encoding UTF8|ConvertFrom-Json;if($managed.missing.Count-or!$managed.packages.Count-or!$managed.runtimeTexts){throw 'Full managed/runtime license inventory required'}
Copy-Item -LiteralPath $ManagedNotices -Destination (Join-Path $stage 'notices/managed') -Recurse
if($SourceUrl.Scheme-ne'https'-or$SourceUrl.UserInfo-or!(Test-Path -LiteralPath $SourceBundle)){throw 'Prepared corresponding source bundle and public HTTPS source URL are required'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceArchive=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($SourceBundle))
try{$inventoryEntry=$sourceArchive.GetEntry('inventory.json');if(!$inventoryEntry-or$inventoryEntry.Length-gt131072){throw 'Source bundle inventory missing/oversized'};$reader=New-Object IO.StreamReader($inventoryEntry.Open());try{$inventory=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()};$supply=Get-Content -LiteralPath (Join-Path $repo 'third_party/ffmpeg/supply.json') -Encoding UTF8|ConvertFrom-Json;if($inventory.recipeCommit-ne'9acad4a9ef1583096af7836cc1e9c8cbcb4d3950'-or$inventory.archiveSha256-ne$supply.sha256){throw 'Corresponding source inventory is incomplete'};$pins=Get-Content -LiteralPath (Join-Path $repo 'third_party/ffmpeg/source-recipes.json') -Raw -Encoding UTF8|ConvertFrom-Json;foreach($pin in $pins.sources){$record=@($inventory.sources|Where-Object {$_.repo-eq$pin.repo-and$_.commit-eq$pin.ref});if($record.Count-ne1-or!$record[0].licenseFiles.Count){throw 'Pinned corresponding dependency/source license missing'};foreach($license in $record[0].licenseFiles){if(!$sourceArchive.GetEntry(($record[0].name+'/'+$license.Replace('\','/')))){throw 'Corresponding license text missing from archive'}}}}finally{$sourceArchive.Dispose()}
[IO.File]::WriteAllText((Join-Path $stage 'notices/SOURCE.txt'),('Corresponding source: '+$SourceUrl.AbsoluteUri+[Environment]::NewLine+'SHA256: '+(Get-FileHash -LiteralPath $SourceBundle).Hash.ToLowerInvariant()),(New-Object Text.UTF8Encoding($false)))
Copy-Item -LiteralPath (Join-Path $repo 'deploy/runbooks') -Destination $stage -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'deploy/install.ps1') -Destination $stage
Copy-Item -LiteralPath ([IO.Path]::GetFullPath($Settings)) -Destination (Join-Path $stage 'host.settings.json')
foreach($file in Get-ChildItem -LiteralPath $stage -Recurse -File|Where-Object {($_.Name-like'RemoteAssist*.dll')-or($_.Name-like'RemoteAssist*.exe')-or($_.Name-eq'install.ps1')}){
    $signed=Set-AuthenticodeSignature -FilePath $file.FullName -Certificate $certificate -TimestampServer $TimestampUrl -HashAlgorithm SHA256
    if($signed.Status-ne'Valid'-or!$signed.TimeStamperCertificate){throw ('Signature/timestamp failed: '+$file.Name)}
}
$files=@(Get-ChildItem -LiteralPath $stage -Recurse -File|Sort-Object FullName|ForEach-Object {@{Path=[IO.Path]::GetRelativePath($stage,$_.FullName).Replace('\','/');Size=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
$zip=Join-Path $dest 'package.zip';Add-Type -AssemblyName System.IO.Compression.FileSystem
$output=New-Object IO.FileStream($zip,[IO.FileMode]::CreateNew);$archive=New-Object IO.Compression.ZipArchive($output,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($f in $files){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $stage $f.Path),$f.Path,[IO.Compression.CompressionLevel]::Optimal)|Out-Null}}finally{$archive.Dispose();$output.Dispose()}
$manifest=@{Product='RemoteAssist';Platform='windows-x64';Protocol=1;Version=$Version;ArchiveSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant();Files=$files}
$utf8=New-Object Text.UTF8Encoding($false);$bytes=$utf8.GetBytes(($manifest|ConvertTo-Json -Depth 8 -Compress))
[IO.File]::WriteAllBytes((Join-Path $dest 'release.json'),$bytes)
$rsa=[Security.Cryptography.RSA]::Create();try{$rsa.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($SigningKeyPem)));if($rsa.KeySize-lt3072){throw 'RSA key must be at least 3072 bits'};[IO.File]::WriteAllBytes((Join-Path $dest 'release.sig'),$rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss));[IO.File]::WriteAllText((Join-Path $dest 'trusted-public.pem'),$rsa.ExportSubjectPublicKeyInfoPem(),$utf8)}finally{$rsa.Dispose()}
Copy-Item -LiteralPath (Join-Path $stage 'RemoteAssist.Update.exe') -Destination $dest
[IO.File]::WriteAllText((Join-Path $dest 'candidate.json'),(@{Commit=(& git -C $repo rev-parse HEAD);Version=$Version;ManifestSha256=(Get-FileHash -LiteralPath (Join-Path $dest 'release.json')).Hash.ToLowerInvariant();ArchiveSha256=$manifest.ArchiveSha256;CertificateThumbprint=$CertificateThumbprint;BuildUtc=[datetime]::UtcNow.ToString('o')}|ConvertTo-Json),$utf8)
Write-Output ('Signed package prepared: '+$dest)
