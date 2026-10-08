# FFmpeg 개발 공급본 고지

공급본은 FFmpeg `n8.1.3-14-g330caae0c1-20261007`의 Windows x64 shared 빌드다. 원본 FFmpeg commit은 `330caae0c1acccd2222edc52a05940c574561ce5`다. 다운로드 archive와 각 DLL 해시는 상위 `supply.json`과 `libraries.json`을 따른다.

실제 `buildconf.txt`에는 `--enable-version3`가 있다. 따라서 이 공급본을 LGPL-2.1 전용으로 표현하지 않는다. FFmpeg 고정 소스의 LGPLv3 전문, LGPLv3이 참조하는 GPLv3 전문, 상류 LICENSE.md를 함께 보관한다. GPL 라이선스 전문의 제공은 이 빌드가 GPL 옵션을 활성화했다는 뜻이 아니다. 준비 스크립트는 `--enable-gpl`, `--enable-nonfree`, `--enable-libx264`를 거부한다.

FFmpeg 소스: https://github.com/FFmpeg/FFmpeg/tree/330caae0c1acccd2222edc52a05940c574561ce5

배포 archive: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-07-13-07

고정된 공급자 build recipe commit은 `9acad4a9ef1583096af7836cc1e9c8cbcb4d3950`이다. `source-recipes.json`은 recipe의 모든 80개 source 참조를 기록하고 FFmpeg 원본 commit을 추가한다. 두 tag 참조도 조회한 commit으로 고정한다. 비활성 GPL 라이브러리의 소스가 이 광범위한 source inventory에 포함될 수 있으나 제품 DLL에서 GPL 옵션을 활성화하지 않는다.

`scripts/prepare-ffmpeg-sources.ps1`로 소스·submodule·전체 라이선스 파일·patch/build recipe를 준비하고 별도 공개 source ZIP을 제공한다. source ZIP은 바이너리 ZIP과 별개이며 다운로드 URL/hash를 제품 고지에 동봉한다. 스크립트는 누락·미고정 참조·license inventory 누락 시 실패한다. 실제 source bundle 생성·재빌드·대응 검증·배포 의무 검토는 V02/V36에서 수행한다. source inventory만으로 법적 준수나 바이너리 재현 성공을 선언하지 않는다. 공급자 toolchain timestamp에 따라 재빌드 hash가 달라질 수 있으므로 바이너리 공급 hash를 임의로 갱신하지 않는다.

사용자는 동적 LGPL DLL을 별도 파일로 교체하고 자신의 목적을 위한 수정 및 그 수정을 디버깅하기 위한 역공학을 할 수 있다. 보안 hash 검사는 원격 지원의 특권 경계이므로 변경 DLL은 관리자가 검토·신뢰 키로 재패키징해야 한다. 원본·수정 소스를 제공받는 방법과 라이선스 전문은 동봉 고지 및 source ZIP에 있다. LGPL 라이선스와 별도로 H.264 구현·사용/배포의 특허 조건은 배포자가 별도 검토한다. 자체 빌드 OpenH264 사용을 Cisco 제공 바이너리의 특허 조건과 같은 것으로 표현하지 않는다.
