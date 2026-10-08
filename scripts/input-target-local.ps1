[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$RunDir,[ValidateRange(30,900)][int]$Seconds=180)
$ErrorActionPreference='Stop'
$repository=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$RunDir=[IO.Path]::GetFullPath($RunDir)
if(!$RunDir.StartsWith((Join-Path $repository '.artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw '격리된 실증 artifact 경로가 필요하다'}
$target=Join-Path $repository 'tools\RemoteAssist.InputTarget\bin\Debug\net10.0-windows10.0.19041.0\RemoteAssist.InputTarget.exe'
if(!(Test-Path -LiteralPath $target -PathType Leaf)){throw '시험 전에 InputTarget을 빌드해야 한다. 이 실행기는 빌드나 설치를 수행하지 않는다.'}
& $target --output $RunDir --seconds $Seconds
if($LASTEXITCODE -ne 0){throw "입력 시험 창이 종료 코드 $LASTEXITCODE 로 실패했다"}
