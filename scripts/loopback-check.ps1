[CmdletBinding()]
param([ValidateRange(25001,25199)][int]$Port=25080,[string]$Output)
# 한 PC 루프백 통합 검사: 개발 모드 시그널링을 loopback에 띄우고 LoopbackCheck(실제 Host 세션 + 가짜 Viewer)를 실행한다.
# 입력은 주입하지 않고 기록만 한다. 화면은 읽기만 한다(약 20초). 서비스 설치·UAC 변경은 없다.
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$run=Join-Path $repo ('.artifacts/loopback-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $run|Out-Null
if(!$Output){$Output=Join-Path $run 'result.json'}
Push-Location (Join-Path $repo 'server/signaling')
try{& go build -o (Join-Path $run 'signaling.exe') .;if($LASTEXITCODE-ne0){throw 'signaling build failed'}}finally{Pop-Location}
& dotnet build (Join-Path $repo 'tools/RemoteAssist.LoopbackCheck') -c Release -v q --nologo|Out-Null
if($LASTEXITCODE-ne0){throw 'LoopbackCheck build failed'}
$env:RA_MODE='development';$env:RA_LISTEN="127.0.0.1:$Port"
$server=Start-Process -FilePath (Join-Path $run 'signaling.exe') -PassThru -NoNewWindow -RedirectStandardOutput (Join-Path $run 'signaling.out.txt') -RedirectStandardError (Join-Path $run 'signaling.err.txt')
try{
    Start-Sleep -Milliseconds 500
    & dotnet run --project (Join-Path $repo 'tools/RemoteAssist.LoopbackCheck') -c Release --no-build -- --signaling "http://127.0.0.1:$Port" --out $Output
    $exit=$LASTEXITCODE
}finally{
    if(!$server.HasExited){Stop-Process -Id $server.Id -Force}
    Remove-Item Env:RA_MODE,Env:RA_LISTEN -ErrorAction SilentlyContinue
}
Write-Output ('Loopback 결과: '+$Output)
exit $exit
