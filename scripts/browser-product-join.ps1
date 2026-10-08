[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDir,[ValidatePattern('^[a-z0-9-]{1,40}$')][string]$BrowserSession='knockhb-media')
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RunDir=[IO.Path]::GetFullPath($RunDir)
if(!$RunDir.StartsWith((Join-Path $repository '.artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '로컬 실증 artifact 경로가 필요하다'}
$pairing=Get-Content -LiteralPath (Join-Path $RunDir 'pairing.json') -Encoding utf8 -Raw | ConvertFrom-Json
$script='async page => { await page.locator("#name").fill("제품 서비스 화면 시험"); await page.locator("#code").fill('+($pairing.code | ConvertTo-Json -Compress)+'); await page.locator("#pin").fill('+($pairing.pin | ConvertTo-Json -Compress)+'); await page.locator("#join-form button").click(); return {width:await page.locator("#screen").evaluate(v=>v.videoWidth),status:await page.locator("#status").textContent()}; }'
$path=Join-Path $RunDir 'browser-join.js'
[IO.File]::WriteAllText($path,$script,(New-Object Text.UTF8Encoding($false)))
$output=& playwright-cli ('-s='+$BrowserSession) run-code --filename $path
($output -join "`n") -split '### ' | Where-Object {$_ -match '^Result'}
if($LASTEXITCODE -ne 0 -or ($output -join "`n") -match '### Error'){throw '브라우저 참가 실패. 비밀 인자가 포함될 수 있으므로 원문 출력하지 않는다'}
