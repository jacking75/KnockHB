# FFmpeg 8.1 공급과 로드

바이너리는 Git에 커밋하지 않는다. scripts/prepare-ffmpeg.ps1이 불변 release archive를 다운로드하고 hash·ABI·옵션을 확인해 third_party/ffmpeg/bin에 배치한다. 기존 공급본을 덮어쓰지 않으며 새 staging 경로는 -Destination으로 지정한다.

## 현재 고정 공급본

| 항목 | 값 |
|---|---|
| 빌드 | n8.1.3-14-g330caae0c1-20261007 Windows x64 LGPL shared |
| release | BtbN/FFmpeg-Builds autobuild-2026-10-07-13-07 |
| 원본 FFmpeg commit | 330caae0c1acccd2222edc52a05940c574561ce5 |
| archive SHA-256 | 30fdaaeb116730fdd6432c2663af0e9ecdf1d6cc387ac085cc1faf8494942510 |
| ABI | FFmpeg 8.1; avcodec62/avformat62/avutil60/avfilter11/avdevice62/swresample6/swscale9 |
| 실제 라이선스 옵션 | enable-version3 있음; enable-gpl/nonfree/libx264 없음 |
| 인코더 정책 | nvenc → qsv → amf → mf → openh264; libx264 금지 |

정확한 URL은 supply.json, 각 DLL hash는 libraries.json에 있다. 준비 스크립트는 archive SHA와 내장할 DLL manifest를 둘 다 검사한다. 실행 시 FFmpegLoader는 assembly에 포함한 libraries.json을 기준으로 누락·변조·다른 공급본을 거부한다. manifest 파일과 DLL을 함께 임의 변경하는 것으로 실행 검사를 우회하지 못한다.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-ffmpeg.ps1
dotnet run --project tools/RemoteAssist.HostRun -- --encoder-check
```

FFmpegInit.Initialise에는 검증한 보호 경로를 명시한다. 배포 위치의 ffmpeg 또는 runtimes/win-x64/native, 개발 트리의 third_party/ffmpeg/bin을 찾으며 시스템 PATH fallback은 없다. 제품 설치본의 보호 ACL은 E36에서 구현해야 한다.

## 고지와 대응 소스

notices에는 이 commit의 COPYING.LGPLv3, LGPLv3이 참조하는 COPYING.GPLv3, 상류 LICENSE.md와 NOTICE.md를 보관한다. 실제 공급본은 enable-version3이므로 LGPL-2.1 전용으로 표기하지 않는다. 준비 스크립트는 notices를 공급 staging에도 복사한다.

상류 FFmpeg 소스는 https://github.com/FFmpeg/FFmpeg/tree/330caae0c1acccd2222edc52a05940c574561ce5 에 있다. 공급 archive는 https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-07-13-07 에 있다.

설치본의 교체 가능한 동적 DLL, 전체 외부 라이브러리 고지와 대응 소스 제공, 특허·배포 의무 검토는 아직 미완료다. 이 출처·옵션·hash 검사만으로 제품 법적 준수나 정식 배포 준비를 선언하지 않는다. 진행은 루트 E02/E36에서 관리한다.

## 현재 머신 검사와 과거 기록

2026-10-08 로컬 synthetic 1080p 검사에서 nvenc/mf/openh264는 출력 60/60·디코딩 60/60·강제 키프레임 fresh decoder 복구를 통과했다. qsv/amf는 UNSUPPORTED였다. 실제 두 PC 수치는 [두 PC 실연결 시험](../../docs/README.md#e2e-status)의 현재 상태와 남은 과제를 따른다. 캡처·네트워크·브라우저 표시 FPS 또는 다른 GPU 성공으로 해석하지 않는다.

2026-08-24에 n8.1.2-44-g7c533d0f86을 사용한 기록은 과거 개발 환경 이력이다. 현재 공급본 hash와 혼용하지 않는다.