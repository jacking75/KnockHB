# 최초 관리자 실행과 연결 준비

2026-10-23 사용자 결정: 대상자가 프로그램을 관리자 권한으로 한 번 실행하면 지원이 시작되고, 그 뒤 대상자는 아무것도 하지 않는다. 지원자는 고정 지원 ID로 접속해 모든 기능을 쓴다.

## 실행 순서

1. 대상자가 `RemoteAssist.Update.exe`(정식은 시작 메뉴 **RemoteAssist Support**, 개발 설치는 `Program Files\RemoteAssist-Dev\RemoteAssist.Update.exe`)를 실행하고 Windows UAC를 승인한다.
2. 런처가 지원 허가(`ProgramData/RemoteAssist/initial-support.json`)를 쓴다. 허가는 현재 콘솔 사용자 SID·Windows 세션·로그온 시각에 묶이고 ACL은 SYSTEM/Administrators만 허용한다.
3. 설치 프로세스의 관리자 토큰으로 방화벽(`prepare-host.ps1`)과 SAS 정책(`prepare-sas.ps1`)을 준비하고 검증한다. 실행 파일 경로·포트에 한정하며 방화벽 전체나 UAC를 끄지 않는다.
4. 서비스를 다시 시작한다. 서비스는 허가를 읽고 Tray와 Helper를 띄워 지원 ID로 대기한다. 런처는 서비스 준비 신호를 확인한 뒤 끝난다(허가는 남는다).

## 허가의 수명

| 사건 | 허가 |
|---|---|
| 지원자 이탈·재접속 | 유지. 서비스가 1초 뒤 다시 대기 |
| 서비스 재시작·업데이트·서비스 crash | 유지. 서비스가 시작할 때 다시 읽는다 |
| 재부팅·빠른 시작 종료·로그오프 | 무효(로그온 시각 불일치). 다시 관리자 실행 |
| Windows 사용자 변경 | 무효. 서비스가 지운다 |
| 대상자의 즉시 종료·Tray 창 닫기 | 지운다 |
| 실행 실패 | 런처가 이번에 쓴 허가를 지운다 |

## 방화벽

`RemoteAssist.Host.Helper.exe`의 WebRTC UDP 27002~27099(수신·송신)와 Helper/Service의 터널링 서버 TCP(443, 설정 포트) 송신 규칙을 버전 경로에 묶어 등록한다. 서비스는 시작할 때 Verify만 수행한다. 정식 설치에서 검증이 실패하면 서비스를 시작하지 않는다. 개발 실행(개발 설치·빌드 출력)에서는 검증 실패를 경고로 기록하고 계속한다.

## SAS 사전 준비

Ctrl+Alt+Del 전송에 필요한 `SoftwareSASGeneration`을 최초 실행에서 준비한다. 값이 없으면 서비스 허용(1), 접근성 앱만 허용(2)이면 양쪽 허용(3)으로 설정한다. 명시적 금지(0)나 알 수 없는 값은 준비 실패다. 원래 값은 `ProgramData/RemoteAssist/startup-sas.json`에 먼저 기록하고 제거할 때 우리가 설정한 값일 때만 복구한다. [Microsoft 정책 설명](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-winlogon#softwaresasgeneration)

## 검증

준비 계획·규칙 범위·충돌·부분 실패 rollback·허가 결합 조건은 입력 없는 테스트(`tests/test-startup-*.ps1`, Host.Core 테스트)로 확인한다. 실제 서비스·방화벽·UAC 변경과 두 PC 연결은 실연결 검증 단계에서 확인한다. 준비 검사 성공은 기능 PASS가 아니다.
