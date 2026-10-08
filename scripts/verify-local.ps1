param([string]$Output, [switch]$Native)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Output) { $Output = Join-Path $root ('.artifacts/local-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw '기존 검증 증거를 덮어쓰지 않는다. 새 Output 경로가 필요하다.' }
New-Item -ItemType Directory -Path $Output | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$results = New-Object 'System.Collections.Generic.List[object]'
function Invoke-Check([string]$Name, [string]$File, [string[]]$Arguments, [string]$Directory) {
    $program = Get-Command -Name $File -CommandType Application -ErrorAction Stop | Select-Object -First 1
    Push-Location -LiteralPath $Directory
    try {
        $savedPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $lines = @(& $program.Source @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = $savedPreference
        $text = ($lines | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine
        [IO.File]::WriteAllText((Join-Path $Output ($Name + '.txt')), $text, $utf8)
        $results.Add(@{ name=$Name; exitCode=$exitCode; command=$File; arguments=$Arguments })
        Write-Output $text
        if ($exitCode -ne 0) { throw "$Name 실패다. 종료 코드: $exitCode" }
    } finally { Pop-Location }
}
try {
    Invoke-Check 'restore' 'dotnet' @('restore','RemoteAssist.slnx','--locked-mode') $root
    Invoke-Check 'build' 'dotnet' @('build','RemoteAssist.slnx','--no-restore','-v','minimal') $root
    Invoke-Check 'core-tests' 'dotnet' @('run','--project','tests/RemoteAssist.Host.Core.Tests','--no-build') $root
    Invoke-Check 'go-build' 'go' @('build','./...') (Join-Path $root 'server/signaling')
    Invoke-Check 'go-executable' 'go' @('build','-o',(Join-Path $Output 'signaling.exe'),'.') (Join-Path $root 'server/signaling')
    Invoke-Check 'go-tests' 'go' @('test','./...') (Join-Path $root 'server/signaling')
    Invoke-Check 'go-vet' 'go' @('vet','./...') (Join-Path $root 'server/signaling')
    Invoke-Check 'viewer-tests' 'node' @('--test','web/viewer/tests/*.test.js') $root
    if ($Native) {
        Invoke-Check 'encoders' 'dotnet' @('run','--project','tools/RemoteAssist.HostRun','--no-build','--','--encoder-check','--out',(Join-Path $Output 'encoders')) $root
        Invoke-Check 'native-media-nvenc' 'dotnet' @('run','--project','tools/RemoteAssist.MediaProbe','--no-build') $root
        Invoke-Check 'native-media-openh264' 'dotnet' @('run','--project','tools/RemoteAssist.MediaProbe','--no-build','--','--encoder','libopenh264') $root
    }
} finally {
    $sha = (& git -C $root rev-parse HEAD | Out-String).Trim()
    $dirty = (& git -C $root status --porcelain | Out-String).Trim().Length -gt 0
    [IO.File]::WriteAllText((Join-Path $Output 'result.json'), (@{ utc=[DateTimeOffset]::UtcNow.ToString('o'); commit=$sha; dirty=$dirty; checks=@($results.ToArray()) } | ConvertTo-Json -Depth 6), $utf8)
}
Write-Output "로컬 검증 증거: $Output"
