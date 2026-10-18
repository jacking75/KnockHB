# Windows 설치와 업데이트

## 소유 테스트 PC의 서명 없는 기능 검증

사용자가 명시적으로 허용한 개발 시험에는 `scripts/package-host-dev.ps1`로 서명 없는 자체 포함 후보를 별도로 만든다. 이 후보는 상용 updater의 서명 검사를 통과하는 release가 아니며 정식 설치/업데이트 수용으로 세지 않는다. 상용 서명 검사와 제품의 코드/PIN·대상자 동의·TLS·권한 경계는 변경하지 않는다. 준비 스크립트는 publish/manifest만 수행하며 서비스를 설치하거나 실행하지 않는다. 보호 설치·소유 정리·시간 제한의 준비 리뷰 후 별도 개발 설치 절차를 제공한다.

현재 머신의 SDK10.0.301을 명시 선택한 개발 빌드는 해당 MSBuild를 직접 사용하고 candidate.json에 SDK와 소스 변경 상태를 기록한다. 저장소 global.json의 상용 후보 SDK10.0.400 고정값은 유지한다. 소스/설정/hash가 다른 후보의 결과를 상용 수용으로 승계하지 않는다.

`scripts/package-host.ps1`는 로컬 배포 명령이다. CI를 만들거나 실행하지 않는다. PowerShell 7·.NET 10 SDK·검증된 FFmpeg DLL·실제 Authenticode 인증서/HTTPS timestamp·RSA 3072-bit 이상 PEM 개인 키를 준비한다. 개인 키를 저장소나 패키지에 넣지 않는다. 모든 버전의 Host는 자체 포함 publish이며 LGPL DLL은 별도 `ffmpeg/`에 둔다. Runbook 두 스크립트·라이선스 고지·설정·manifest도 동봉한다. 대응소스 bundle 준비가 실패하면 배포하지 않는다.

패키징 예시는 `pwsh scripts/package-host.ps1 -Version 1.0.0.0 -Settings <검토한 host.settings.json> -CertificateThumbprint <제품 인증서> -TimestampUrl <HTTPS timestamp> -SigningKeyPem <보호된 개인 키> -SourceBundle <준비한 소스 ZIP> -SourceUrl https://<공개 소스 URL> -ManagedNotices <준비한 전체 고지 디렉터리> -Output <새 디렉터리>`다. 소스 archive는 `prepare-ffmpeg-sources.ps1`로 별도 준비·공개하며 바이너리 고지에 URL/hash를 동봉한다. 정식 인증서가 없으면 V36은 BLOCKED다. 개발 인증서를 제품 서명으로 표현하지 않는다. Windows 대상 머신의 TrustedPublisher에 검토한 제품 서명 인증서를 등록하며 installer는 AllSigned 정책으로 실행한다. 설치 후 인증서 추가를 자동 승인하지 않는다.

최초 설치자는 배포자 인증서 체인과 제공된 공개 키 fingerprint를 별도 경로로 확인한다. `Get-AuthenticodeSignature RemoteAssist.Update.exe`가 Valid이며 제품 배포자와 timestamp가 일치해야 한다. 서명된 단일 파일 updater를 관리자 PowerShell에서 `RemoteAssist.Update.exe install <release.json> <release.sig> <package.zip> <신뢰 공개 키.pem>`으로 실행한다. ZIP 안의 updater를 검증 전에 실행하지 않는다. single-file 묶음은 LGPL Host와 분리돼 있다. 운영자는 보호된 `Program Files/RemoteAssist/trusted-public.pem`과 `scripts/configure-updates.ps1 -Endpoint https://<운영주소>/release/`로 업데이트 키/주소를 고정한다. endpoint는 동일 서명의 release.json/release.sig/package.zip을 제공한다. redirect는 거부한다.

지원 중에는 설치/제거/업데이트 gate를 얻을 수 없어 교체를 시작하지 않는다. 신규 설치·동일 버전 재설치·파일 변조/누락 수리는 검증된 package로 같은 명령을 사용한다. 수리는 별도 version-repair 디렉터리에 설치하며 기존 디렉터리를 덮어쓰지 않는다. 서비스 경로 전환 전 pending transaction을 기록하고 config/dependency self-check 준비 신호를 45초 확인한다. 이 신호는 화면/입력 PASS가 아니다. 실패 시 직전 image를 복구하며 자동 recovery 작업은 pending transaction만 처리한다. 최고 승인 버전은 rollback으로 낮추지 않는다. 정상 commit 이전에 전원이 끊기면 다음 boot recovery가 복구하며 V37에서 실증한다.

제거는 관리자가 `Program Files/RemoteAssist/RemoteAssist.Update.exe remove`를 실행한다. 소유 서비스/스케줄 작업/version 디렉터리를 정리하며 수신 파일과 ProgramData의 감사/상태는 보존한다. 실행 중 updater 파일 잠금은 `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`로 정확한 소유 파일만 다음 재부팅 때 삭제 예약한다. 제품 재접속은 사라지지만 파일 정리를 마치려면 재부팅이 필요하다고 안내한다.

자동 업데이트 task는 매일 03:00 SYSTEM으로 실행하며 boot recovery task는 부팅 시 실행한다. 계정/인증서/TLS/TURN/업데이트 운영 자원과 VM 실제 설치/rollback은 V36/V37/F01/F12에서 별도로 판정한다.

관리형 의존성 고지는 prepare-managed-notices.ps1로 lock/contentHash·NuGet의 전체 license/notice·런타임 고지를 수집한다. 패키지 안에 전문이 없으면 검토한 LicenseDirectory의 SPDX 전체 문서를 제공한다. 미해결 목록이 남으면 패키징하지 않는다. 초기 root updater는 protocol 1의 복구 bootstrap으로 유지하고 매일 작업에서는 현재 검증된 서비스 버전의 updater로 전달한다. 따라서 새 버전 updater도 자동 적용하며 bootstrap/journal schema나 신뢰 키를 바꿀 때는 별도 서명된 관리자 유지보수를 수행한다. 실패 staging/download는 정리하고 24시간 지난 GUID staging도 정리한다. 정상 버전은 rollback을 위해 보존하며 유휴 관리자 유지보수로만 제거한다.
