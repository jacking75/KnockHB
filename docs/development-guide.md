# 실행·로컬 검증 가이드

진행 상태는 [구현계획](../docs_working/구현계획-1.md) 하나에서 관리한다. CI와 대화형 화면 시험을 자동 추가하지 않는다.

## 비대화형 검사

Windows x64, global.json의 .NET 10 SDK/WindowsDesktop, Go 1.27, Node 22 이상을 준비한다.

```powershell
dotnet restore RemoteAssist.slnx --locked-mode
dotnet build RemoteAssist.slnx --no-restore
dotnet run --project tests/RemoteAssist.Host.Core.Tests --no-build
cd server/signaling; go vet ./...; go test ./...; cd ../..
node --test web/viewer/tests/*.test.js
pwsh -NoProfile -File scripts/verify-local.ps1   # 위 검사 묶음 + 설치 계약 검사, 결과를 .artifacts에 기록
```

`tests/test-startup-preparation.ps1`, `tests/test-startup-sas.ps1`, `tests/test-support-installer.ps1`, `tests/installer-contract.ps1`은 방화벽/SAS/설치 스크립트의 함수만 불러 검사하며 시스템을 바꾸지 않는다.

인코더 장치 검증은 화면·네트워크 없이 실행한다.

```powershell
dotnet run --project tools/RemoteAssist.HostRun -c Release -- --encoder-check --out .artifacts/encoders/<새 폴더>
```

## 개발 설치

1. 시그널링 TLS 인증서를 준비한다. 사설망은 `python scripts/prepare-e2e-tls.py --directory <새 폴더> --ip <서버 IP>`로 7일짜리 자체 서명 인증서(`server-cert.pem`, `server-key.pem`, DER `lan-test.cer`)를 만들 수 있다. 지원자 브라우저에는 이 인증서를 신뢰시키거나 첫 접속 때 예외를 허용한다.
2. 시그널링 서버를 띄운다.
   ```powershell
   cd server/signaling
   $env:RA_MODE='lan'; $env:RA_LISTEN='0.0.0.0:25001'; $env:RA_TLS_CERT='<폴더>/server-cert.pem'; $env:RA_TLS_KEY='<폴더>/server-key.pem'; $env:RA_VIEWER_DIR='../../web/viewer'
   go run .
   ```
3. Host 후보를 만든다. 자체 서명이면 DER 인증서를 핀 고정한다.
   ```powershell
   pwsh scripts/package-host-dev.ps1 -SignalingUri https://<서버 IP>:25001/ -SignalingCertificate <폴더>/lan-test.cer -SdkDirectory <.NET 10 SDK 폴더> -Output .artifacts/<새 폴더>
   ```
4. 대상 PC의 관리자 PowerShell에서 설치한다: `scripts/install-host-dev.ps1 -Action Install -Package .artifacts/<폴더>`.
5. 대상 사용자가 `Program Files\RemoteAssist-Dev\RemoteAssist.Update.exe`를 관리자 권한으로 실행한다. Tray에 지원 ID가 뜬다.
6. 지원자 PC 브라우저에서 `https://<서버 IP>:25001/`을 열고 지원 ID로 접속한다.
7. 제거: `scripts/install-host-dev.ps1 -Action Remove`.

개발 설치와 빌드 출력 실행은 개발 모드다. 보호 경로 ACL·릴리스 manifest 검사를 생략하므로 시험 PC에서만 쓴다.

## 실연결 시험

두 PC 실연결 시험은 화면·입력을 점유한다. AGENTS.md 지침대로 실행 계획(목적·단계·판정·증거·중단/정리 절차)을 먼저 문서화하고, 사용자에게 시험 여부를 묻고 승인받은 뒤에만 실행한다. 원인과 준비가 바뀌지 않은 같은 시험은 반복하지 않는다. 화면 점유 시간과 결과는 실측값으로 기록한다.

## 공급·고지

`prepare-ffmpeg.ps1`은 고정 archive/SHA/buildconf/7DLL hash/ABI를 검사해 배치한다. `prepare-ffmpeg-sources.ps1`·`rebuild-ffmpeg.sh`는 LGPL 대응 소스 자료를 만든다. `prepare-managed-notices.ps1`은 NuGet 라이선스 고지를 모은다. 정식 패키징은 [설치 가이드](../deploy/installation.md)를 따른다.
