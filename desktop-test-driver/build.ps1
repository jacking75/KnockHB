param([switch]$Package)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet build DesktopTestDriver.slnx -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Independent build failed' }
    if ($Package) {
        $target = Join-Path $PSScriptRoot ('artifacts/package-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $target | Out-Null
        foreach ($project in @('Client','Client.Broker','Client.Watchdog','Server')) {
            dotnet publish "src/$project/$project.csproj" -c Release -r win-x64 --self-contained false -o (Join-Path $target $project) --nologo
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
        }
        $transportTarget = Join-Path $target 'transport'
        New-Item -ItemType Directory -Path $transportTarget | Out-Null
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'transport') -File | Copy-Item -Destination $transportTarget
        $files = @(Get-ChildItem -LiteralPath $target -Recurse -File | Where-Object { $_.FullName -notmatch '[\\/]__pycache__[\\/]' } | Sort-Object FullName | ForEach-Object {
            @{ path=$_.FullName.Substring($target.Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); size=$_.Length }
        })
        @{version='0.1.0';protocolVersion=1;files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $target 'package-manifest.json') -Encoding UTF8
        Write-Output $target
    }
} finally { Pop-Location }
