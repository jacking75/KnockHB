param([switch]$RemoteAssist)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $python = Join-Path $PSScriptRoot '.venv/Scripts/python.exe'
    if (!(Test-Path -LiteralPath $python)) { throw 'Create the local .venv and install transport/requirements.txt first' }
    $evidence = Join-Path $PSScriptRoot ('artifacts/verify-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $evidence | Out-Null
    & ./build.ps1 -Package -RemoteAssist:$RemoteAssist | Tee-Object -FilePath (Join-Path $evidence 'build.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    $package = (Get-ChildItem -Path 'artifacts/package-*/package-manifest.json' | Sort-Object LastWriteTime -Descending | Select-Object -First 1).DirectoryName
    & ./tests/Driver.Tests/bin/Release/net10.0-windows10.0.19041.0/Driver.Tests.exe | Tee-Object -FilePath (Join-Path $evidence 'contracts.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Contract/fault test failed' }
    $previousPackage = $env:DTD_TEST_PACKAGE
    try {
        $env:DTD_TEST_PACKAGE = $package
        & $python tests/test_ssh_transport.py | Tee-Object -FilePath (Join-Path $evidence 'ssh.txt')
        if ($LASTEXITCODE -ne 0) { throw 'SSH mock test failed' }
        & $python tests/test_cli.py | Tee-Object -FilePath (Join-Path $evidence 'scenario-cli.txt')
        if ($LASTEXITCODE -ne 0) { throw 'Scenario/signing CLI test failed' }
        if (!$RemoteAssist) {
            & $python tests/test_independent.py | Tee-Object -FilePath (Join-Path $evidence 'independent.txt')
            if ($LASTEXITCODE -ne 0) { throw 'Core-only source export failed' }
        }
        if ($RemoteAssist) {
            & $python adapters/remoteassist/generate_plans.py --check | Tee-Object -FilePath (Join-Path $evidence 'plans.txt')
            if ($LASTEXITCODE -ne 0) { throw 'V/F plan drift' }
        }
    } finally { $env:DTD_TEST_PACKAGE = $previousPackage }
    & (Join-Path $package 'Server/Server.exe') validate inventory/example.json | Tee-Object -FilePath (Join-Path $evidence 'cli.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Published CLI validation failed' }
    @{ at=[DateTimeOffset]::UtcNow.ToString('O'); package=$package; evidence=$evidence; profile=$(if($RemoteAssist){'remoteassist'}else{'core'}); realDesktopInput=$false; twoPc='NOT RUN'; plannedE2e='D21-D23 independent driver only; product integration deferred to H stages' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'summary.json') -Encoding UTF8
    Write-Output "Evidence: $evidence"
} finally { Pop-Location }
