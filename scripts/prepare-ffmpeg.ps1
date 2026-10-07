param([string]$Destination = (Join-Path $PSScriptRoot '../third_party/ffmpeg/bin'))
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/supply.json') -Encoding utf8 | ConvertFrom-Json
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('remoteassist-ffmpeg-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    $archive = Join-Path $staging 'ffmpeg.zip'
    Invoke-WebRequest -Uri $pin.url -OutFile $archive -UseBasicParsing
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pin.sha256) { throw 'FFmpeg archive SHA-256 mismatch' }
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $staging 'expanded')
    $exe = Get-ChildItem -LiteralPath (Join-Path $staging 'expanded') -Filter ffmpeg.exe -Recurse | Select-Object -First 1
    if (!$exe) { throw 'ffmpeg.exe missing' }
    $config = (& $exe.FullName -hide_banner -buildconf 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $config -match '--enable-(gpl|nonfree|libx264)') { throw 'Forbidden FFmpeg build configuration' }
    $version = (& $exe.FullName -version 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or $version -notmatch 'ffmpeg version n?8\.1') { throw 'FFmpeg 8.1 is required' }
    $native = Get-ChildItem -LiteralPath $exe.Directory.FullName -Filter '*.dll'
    foreach ($name in @('avcodec-62.dll','avformat-62.dll','avutil-60.dll','avfilter-11.dll','avdevice-62.dll','swresample-6.dll','swscale-9.dll')) {
        if ($name -notin $native.Name) { throw "Required ABI library missing: $name" }
    }
    $records = @($native | ForEach-Object { @{ name=$_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } })
    $trusted = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/libraries.json') -Encoding utf8 | ConvertFrom-Json
    if ($trusted.archiveSha256 -ne $pin.sha256 -or $trusted.files.Count -ne $records.Count) { throw 'Supply and trusted library manifest mismatch' }
    foreach ($record in $records) {
        $expected = @($trusted.files | Where-Object { $_.name -eq $record.name })
        if ($expected.Count -ne 1 -or $expected[0].sha256 -ne $record.sha256) { throw "Trusted library hash mismatch: $($record.name)" }
    }
    $target = [System.IO.Path]::GetFullPath($Destination)
    if (Test-Path -LiteralPath $target) { throw "Destination already exists; verify or choose a new staging path: $target" }
    New-Item -ItemType Directory -Path $target | Out-Null
    foreach ($file in $native) { Copy-Item -LiteralPath $file.FullName -Destination $target }
    Copy-Item -LiteralPath $exe.FullName -Destination $target
    $probe = Join-Path $exe.Directory.FullName 'ffprobe.exe'
    if (Test-Path -LiteralPath $probe) { Copy-Item -LiteralPath $probe -Destination $target }
    [IO.File]::WriteAllText((Join-Path $target 'manifest.json'), (@{ archiveSha256=$pin.sha256; files=$records } | ConvertTo-Json -Depth 5), $utf8)
    [IO.File]::WriteAllText((Join-Path $target 'buildconf.txt'), $config, $utf8)
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../third_party/ffmpeg/notices') -Destination (Join-Path $target 'notices') -Recurse
    Write-Output "Verified FFmpeg 8.1 LGPL shared: $target"
} finally {
    $resolved = [IO.Path]::GetFullPath($staging)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if (!$resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe staging cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
