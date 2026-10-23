# e2e-preflight/e2e-prepare가 dot-source한다. B SSH 인증을 키 또는 비밀번호 환경 변수로 준비한다.
# 비밀번호 값은 출력·기록하지 않는다. 환경 변수 이름만 받아 SSH_ASKPASS 도우미가 그 값을 읽는다.
function Initialize-E2ESsh([string]$PasswordEnv) {
    if(!$PasswordEnv){return @('-o','BatchMode=yes')}
    if(![Environment]::GetEnvironmentVariable($PasswordEnv)){throw "환경 변수 $PasswordEnv 가 비어 있다."}
    $askpass=Join-Path ([IO.Path]::GetTempPath()) 'remoteassist-e2e-askpass.cmd'
    # 도우미에는 변수 이름만 들어간다.
    [IO.File]::WriteAllText($askpass,"@powershell -NoProfile -Command ""[Console]::Out.Write(`$env:$PasswordEnv)""`r`n",[Text.Encoding]::ASCII)
    $env:SSH_ASKPASS=$askpass;$env:SSH_ASKPASS_REQUIRE='force';if(!$env:DISPLAY){$env:DISPLAY='e2e'}
    @('-o','PreferredAuthentications=password,keyboard-interactive','-o','NumberOfPasswordPrompts=1')
}
