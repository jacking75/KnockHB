# FFmpeg (동봉 대상)

이 디렉터리에는 Host가 로드할 **FFmpeg shared library**를 넣는다.
바이너리는 저장소에 커밋하지 않는다(.gitignore 처리). 빌드·설치 단계에서 배치한다.

## 현재 배치본 (2026-08-24)

| 항목 | 값 |
|---|---|
| 빌드 | `ffmpeg-n8.1-latest-win64-lgpl-shared` (BtbN/FFmpeg-Builds) |
| 버전 | `n8.1.2-44-g7c533d0f86-20260824` |
| 크기 | 137MB (DLL 7개) |
| 검증 | `--enable-gpl` 없음 · `--disable-libx264` 확인 |

확인 명령:

```
ffmpeg -hide_banner -buildconf | grep -E "enable-gpl|x264"
ffmpeg -hide_banner -encoders | grep 264
```

## 요구사항

| 항목 | 값 |
|---|---|
| ABI 버전 | **FFmpeg 8.1** — `SIPSorceryMedia.FFmpeg`가 `FFmpeg.AutoGen 8.1.0`으로 8.1 ABI를 타깃한다. 버전이 어긋나면 PInvoke 로드 에러가 난다 |
| 빌드 종류 | **LGPL 빌드만.** `--enable-gpl` 로 빌드된 것을 쓰지 않는다 |
| 금지 | **libx264 포함 빌드 금지.** libx264는 GPL이라 동봉 시 제품 전체가 GPL 조건에 걸린다 |
| H.264 인코더 | HW 우선: `h264_nvenc` / `h264_qsv` / `h264_amf` / `h264_mf`. 없으면 `libopenh264` SW 폴백 |
| 링크 방식 | 비수정 공유 라이브러리를 **동적 링크**. 단일 실행파일로 합치지 않는다 |

## LGPL 준수 체크리스트 (T6.1에서 확인)

- [ ] LGPL-2.1 라이선스 전문을 설치본에 포함
- [ ] 사용한 FFmpeg 버전·빌드 옵션 명시
- [ ] 해당 FFmpeg 소스 제공(또는 서면 제안) 경로 마련
- [ ] FFmpeg·`SIPSorceryMedia.FFmpeg`를 교체 가능한 별도 DLL로 유지
- [ ] 실제 선택된 인코더가 `libx264`가 아님을 로그로 확인 (T1.3b DoD)

## 로드 경로

`FFmpegInit.Initialise()` 는 PATH에서 shared lib를 찾는다.
사용자 머신에 설치된 다른 FFmpeg를 잡지 않도록 **동봉 경로를 명시적으로 지정**한다.
