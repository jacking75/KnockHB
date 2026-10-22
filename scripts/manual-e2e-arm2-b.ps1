$ErrorActionPreference='Stop'
$base='C:/Users/jacking75NoteBook/RemoteAssist-Test/20261022-2304'
New-Item -ItemType Directory -Path $base -ErrorAction Stop|Out-Null
Copy-Item 'C:/Users/jacking75NoteBook/RemoteAssist-Test/20261022-2243/guard.ps1' "$base/guard.ps1"
& "$base/guard.ps1" -Mode Arm -RunId 20261022-2304
