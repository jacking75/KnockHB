# FFmpeg 개발 공급본 고지

공급본은 FFmpeg `n8.1.3-14-g330caae0c1-20261007`의 Windows x64 shared 빌드다. 원본 FFmpeg commit은 `330caae0c1acccd2222edc52a05940c574561ce5`다. 다운로드 archive와 각 DLL 해시는 상위 `supply.json`과 `libraries.json`을 따른다.

실제 `buildconf.txt`에는 `--enable-version3`가 있다. 따라서 이 공급본을 LGPL-2.1 전용으로 표현하지 않는다. FFmpeg 고정 소스의 LGPLv3 전문, LGPLv3이 참조하는 GPLv3 전문, 상류 LICENSE.md를 함께 보관한다. GPL 라이선스 전문의 제공은 이 빌드가 GPL 옵션을 활성화했다는 뜻이 아니다. 준비 스크립트는 `--enable-gpl`, `--enable-nonfree`, `--enable-libx264`를 거부한다.

FFmpeg 소스: https://github.com/FFmpeg/FFmpeg/tree/330caae0c1acccd2222edc52a05940c574561ce5

배포 archive: https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-07-13-07

이 자료는 개발 공급본의 출처·옵션·상류 고지다. 설치본의 링크 방식·교체 가능성·함께 빌드된 외부 라이브러리 전체의 고지와 대응 소스 제공·H.264 특허 조건을 검토한 제품 배포 자료는 아직 아니다. E02/E36에서 해당 배포 자료를 완성하고 검증해야 한다.
