# 실행·로컬 검증 가이드

E 전체 구현/코드 리뷰·Gate I 뒤에 V를 실행하고 V/Gate A 뒤에 두 PC F를 별도로 수행한다. 진행 상태는 [구현계획](../원격제어-구현계획.md) 하나다. CI와 대화형 화면 시험을 자동 추가하지 않는다.

## 비대화형 준비·검사

Windows x64, global.json의 .NET10 SDK/WindowsDesktop, Go1.27, Node22 이상을 준비한다. runner는 Windows PowerShell5.1/7이고 서명 패키징은7이다. Viewer npm 설치/번들 단계는 없다. lock restore에 필요한 네트워크/캐시가 필요하다.

```powershell
dotnet restore RemoteAssist.slnx --locked-mode
dotnet build RemoteAssist.slnx --no-restore
pwsh -NoProfile -File scripts/verify-local.ps1
```

runner는 새 ignored artifact에 후보 commit/dirty·명령/시간/deadline/exit/output을 기록하며 실패·출력 상한·timeout은 비정상 종료다. core tests는 순수 계약/파일 handle 등 로컬 검사이고 실제 UI/UAC 수용과 구분한다. Go long load는 기본 skip이며 별도 600..7200초 화면 없는 묶음으로 실행한다. -Race는 C compiler 준비, -Native는 FFmpeg 준비와 synthetic encode/decode 목적의 별도 실행 계획 뒤에 사용한다.

모든 PowerShell 준비/설치 스크립트는 ParseFile 문법 검사와 경로·삭제·PID·ACL·정리 리뷰를 먼저 한다. git diff --check와 후보 hash를 보존한다. 테스트 실패를 skip로 바꾸거나 기존 산출물을 지워 통과시키지 않는다.

## 공급·개발 진단

prepare-ffmpeg.ps1은 고정 archive/SHA/buildconf/7DLL hash/ABI를 검사해 새 경로에 배치한다. prepare-ffmpeg-sources.ps1은 대응 recipe·고정 dependency source/submodule·전체 license 파일을 모으고 rebuild-ffmpeg.sh는 격리 Linux/Docker 재빌드 자료를 기록한다. supplier와 toolchain 차이 때문에 bit-identical을 가정하지 않으며 V02에서 비교한다.

prepare-managed-notices.ps1은 lock/contentHash·전체 NuGet license/notice·런타임 고지를 수집한다. 패키지에 전체 전문이 없으면 검토한 SPDX LicenseDirectory를 제공해야 한다. 누락 inventory는 패키징을 차단한다. [설치 가이드](../deploy/installation.md)의 인증서/PSS/source 공개 조건을 충족한 뒤 package-host.ps1을 사용한다. 실제 서명/새 VM 설치를 SDK 개발 빌드로 대체하지 않는다.

HostRun의 --encoder-check는 화면/네트워크가 없는 synthetic 출력·decode 검사다. 화면 공유 HostRun은 --dev-loopback과 콘솔 '화면 승인'이 필요하다. 제품 화면/입력 동의는 서비스/Tray에서 수행하며 HostRun으로 제품 권한을 자동 부여하지 않는다. CaptureDump/BoundaryProbe/InputTarget의 실제 화면/입력/UAC 실행도 사전 계획이 필요하다.

## 제품 묶음과 중단

[하네스 계약](../tests/e2e/README.md)·[준비표](../docs_working/e2e-test-preparation.md)·scenarios.json에서 한 묶음을 선택한다. build/publish/hash·인증서/포트·계정/Session/desktop·실제 대상 창/좌표/foreground·로그·복귀/소유 정리를 먼저 준비한다.

product-e2e-harness.ps1 Prepare는 화면 없는 준비다. Arm은 reviewed readiness JSON과 후보 hash/실제 Tray 준비를 확인한다. 화면 점유 목적·예상240초/최대300초·실패 중단을 실행 직전 알린다. 대상자 직접 승인 뒤 기능 관찰을 기록한다. 첫 실패/신호 누락/timeout은 Abort→Collect 정리 판정이며 화면 점유 중 수정/탐색/추가 시나리오를 하지 않는다.

Collect는 actualSeconds·실제 evidence 없는 결과를 BLOCKED로 남긴다. 서비스 기동/ready/SSH 성공은 기능 PASS가 아니다. 입력 target·브라우저·방화벽 규칙은 확인한 소유 PID/경로만 정리한다. UAC/잠금은 현장 복귀 담당자와 물리 키보드 종료 수단을 준비한다. 정리 실패는 일반 작업 복구부터 해결한다.

원인/준비가 바뀌지 않은 동일 시험은 반복하지 않는다. 수집한 증거로 결함을 좁히고 변경/판정/정리를 리뷰한 새 Prepare만 허용한다. 두 물리 PC·GPU·다른 NAT·운영 TLS/TURN·정식 서명·전원 차단/HA 환경이 없으면 대응 V/F를 BLOCKED로 남긴다.
