param([switch]$Package, [switch]$RemoteAssist)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $solution = if ($RemoteAssist) { 'DesktopTestDriver.RemoteAssist.slnx' } else { 'DesktopTestDriver.slnx' }
    $buildOptions = @()
    if ($RemoteAssist) { $buildOptions += '-p:IncludeRemoteAssist=true' }
    dotnet build $solution -c Release --nologo @buildOptions
    if ($LASTEXITCODE -ne 0) { throw 'Independent build failed' }
    if ($Package) {
        $target = Join-Path $PSScriptRoot ('artifacts/package-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $target | Out-Null
        foreach ($project in @('Client','Client.Broker','Client.Watchdog','Server')) {
            dotnet publish "src/$project/$project.csproj" -c Release -r win-x64 --self-contained false -o (Join-Path $target $project) --nologo
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
        }
        dotnet publish fixtures/MarkerWindow/MarkerWindow.csproj -c Release -r win-x64 --self-contained false -o (Join-Path $target 'MarkerWindow') --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed: MarkerWindow' }
        if ($RemoteAssist) {
            dotnet publish adapters/remoteassist/RemoteAssist.Adapter.csproj -c Release -r win-x64 --self-contained false -o (Join-Path $target 'adapters/remoteassist') --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Publish failed: optional RemoteAssist adapter' }
        }
        $transportTarget = Join-Path $target 'transport'
        New-Item -ItemType Directory -Path $transportTarget | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'transport') -File | Copy-Item -Destination $transportTarget
        foreach ($folder in @('scenarios','fixtures','docs','cli','inventory')) {
            $destination = Join-Path $target $folder
            New-Item -ItemType Directory -Path $destination | Out-Null
            Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot $folder) -Recurse -File | Where-Object {
                $_.Extension -in @('.json','.md','.py') -and $_.FullName -notmatch '[\\/](bin|obj|__pycache__)[\\/]' -and
                ($RemoteAssist -or ($_.FullName -notmatch '[\\/]scenarios[\\/]remoteassist[\\/]' -and ($folder -ne 'fixtures' -or $_.Name -eq 'generic-desktop.json')))
            } | ForEach-Object {
                $relative = $_.FullName.Substring((Join-Path $PSScriptRoot $folder).Length + 1)
                $copy = Join-Path $destination $relative
                New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force | Out-Null
                Copy-Item -LiteralPath $_.FullName -Destination $copy
            }
        }
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-10.md') -Destination $target
        if ($RemoteAssist) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'adapters/remoteassist/acceptance.json') -Destination (Join-Path $target 'adapters/remoteassist') }
        # Enumerate after copying scenarios/docs so every shipped file is covered by the manifest.
        $files = @(Get-ChildItem -LiteralPath $target -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' } | Sort-Object FullName | ForEach-Object {
            @{ path=$_.FullName.Substring($target.Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); size=$_.Length }
        })
        @{version='0.2.0';protocolVersion=2;profile=$(if($RemoteAssist){'remoteassist'}else{'core'});files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $target 'package-manifest.json') -Encoding UTF8
        Write-Output $target
    }
} finally { Pop-Location }
