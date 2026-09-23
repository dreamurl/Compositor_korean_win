[Compositor](https://github.com/robbietilton/Compositor)(macOS, MIT)를 윈도우에서 쓸 수 있게 다시 만든 한국어 이미지 편집기입니다. 편집 › 환경 설정 › 언어에서 영어와 한국어를 바꿀 수 있습니다.

## 어떤 파일을 받나요

| 파일 | 내용 |
|---|---|
| `Compositor_korean_win-ai-…-setup.exe` | **AI 포함판 설치 파일.** 필터 › 배경 제거(BiRefNet-lite)가 됩니다. 약 150 MB |
| `Compositor_korean_win-…-setup.exe` | **미포함판 설치 파일.** 배경 제거만 빠지고 나머지는 같습니다 |
| `….zip` | 설치 없이 쓰는 휴대용. 압축을 풀고 `Compositor_korean_win.exe`를 실행합니다 |
| `SHA256SUMS.txt` | 파일 무결성 확인용 해시 |

- **설치 위치**: `%LOCALAPPDATA%\Programs\Compositor_korean_win` (사용자별 설치, 관리자 권한 불필요)
- **제거**: 설정 › 앱 › 설치된 앱(또는 제어판 › 프로그램 추가/제거)에서 제거하면 프로그램 폴더와
  설정(`%APPDATA%\Compositor_korean_win`)까지 모두 지워집니다.
- 두 판은 같은 프로그램으로 취급됩니다. 한쪽 위에 다른 쪽을 설치하면 교체됩니다.

## 처음 실행할 때

코드 서명이 없어서 Windows SmartScreen이 "Windows의 PC 보호" 창을 띄울 수 있습니다.
**추가 정보 › 실행**을 누르면 설치됩니다. 파일이 이 저장소에서 받은 것인지는 `SHA256SUMS.txt`로 확인할 수 있습니다.

## 업데이트

도움말 › 업데이트 확인에서 새 버전이 있는지 확인합니다. 앱이 스스로 내려받거나 설치하지는 않으며, 이 페이지를 열어 줍니다.
