# Compositor_korean_win — 작업 규칙

원본 [robbietilton/Compositor](https://github.com/robbietilton/Compositor) 1.0.4(macOS, MIT)를
윈도우용 한국어 이미지 편집기로 **재구현**하는 프로젝트다. 설계와 단계별 계획은
[`docs/windows-port.md`](docs/windows-port.md), 지금까지 한 일의 상세 기록은
[`docs/progress.md`](docs/progress.md)에 있다.

---

## 1. 빌드는 GitHub Actions에서만 한다

**로컬에 .NET SDK가 없고, 설치하지 않는다.** 컴파일·테스트·패키징은 전부 CI에서 돌아간다.
그래서 **푸시해서 CI 결과를 읽는 것이 곧 "빌드해 본다"** 에 해당한다. 각 마일스톤의 완료는
Actions가 초록불이 되는 것으로 확인한다.

작업 흐름은 이렇다. 작업 단위가 끝나면 커밋·푸시하고, 결과를 확인하고, 실패하면 로그를 읽어
고치고 다시 푸시한다. 매번 허락을 묻지 않는다 — 컴파일할 때마다 허락을 구하는 셈이 된다.

## 2. `gh` 인증은 프로젝트 `.env` 의 `GH_PAT` 로 한다

`gh` 는 로그아웃 상태다(전역 자격증명이 철회돼 있다). 토큰은 저장소 루트 `.env` 에 있고
`.gitignore` 에 올라가 있다. **Bash 호출마다 셸 상태가 끊기므로 명령 앞에 매번 붙인다.**

```bash
set -a && . ./.env && set +a && export GH_TOKEN="${GH_TOKEN:-$GH_PAT}" && gh run list -R dreamurl/Compositor_korean_win -L 3
```

자주 쓰는 것들:

```bash
# 최근 실행 상태
gh run list -R dreamurl/Compositor_korean_win -L 3 --json databaseId,status,conclusion,displayTitle --jq '.[] | "\(.databaseId) \(.status) \(.conclusion) \(.displayTitle)"'

# 끝날 때까지 기다리기
gh run watch <id> -R dreamurl/Compositor_korean_win --interval 25

# 실패 원인 — run --log 보다 job 로그가 확실하다
jobid=$(gh run view <id> -R dreamurl/Compositor_korean_win --json jobs --jq '.jobs[0].databaseId')
gh api repos/dreamurl/Compositor_korean_win/actions/jobs/$jobid/logs | grep -E "error C|Failed!|Passed!|\[FAIL\]"
```

**토큰 값을 출력하거나 다른 파일로 옮기지 말 것.** `.env` 를 `cat`/`sed` 로 표시하려 하면
자격증명 취급으로 차단된다 — `.` 으로 읽어 환경변수로만 쓴다.

> 이 절이 있는 이유: 첫 세션에서 CI 결과를 확인하지 못해 한참 막혔다. `gh auth login` 을
> 하라고 안내했지만 실제로는 토큰이 이미 `.env` 에 있었다.

## 3. 저장소 신원

원격은 SSH(`git@github.com:dreamurl/Compositor_korean_win.git`)이고, 저장소 로컬 설정에
키와 신원이 박혀 있다(`core.sshCommand`, `user.name`, `user.email`). **글로벌 git 설정에
의존하지 않는다** — 계정이 여러 개라 섞이면 Vercel/GitHub 쪽에서 사고가 난다. 자세한 것은
`E:\.claude\TOOLS.md`.

## 4. 코드 규칙

- **주석과 XML 문서는 영어, 문서(`docs/*.md`)와 CI 요약은 한국어.** 기존 파일들의 톤을 따른다 —
  "무엇을" 보다 **"왜 이렇게 했는지"** 와 대안을 버린 이유를 적는다.
- `Core` 는 `System.*` 와 C 커널 외에 아무것도 참조하지 않는다. 테스트는 전부 여기에 붙고
  백엔드·디스플레이 없이 돈다.
- `Core` 는 `TreatWarningsAsErrors` 다. 경고 하나도 CI를 빨갛게 만든다.
  셸은 남의 interop 시그니처를 다루므로 경고를 경고로 둔다.
- 픽셀은 전 구간 **프리멀티플 RGBA**, `PixelBuffer` 는 불변·참조계수·unmanaged 다.
  `using` 과 `Release()` 를 **둘 다** 쓰지 말 것(이중 해제로 터진다).
- 커밋 메시지는 영어 산문체. 제목은 한 줄, 본문은 왜 그렇게 했는지.
  끝에 `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

## 5. 이 스택에서 실제로 물린 함정들

- **C 의 `long` 은 윈도우에서 32비트다.** 커널이 `long bounds[4]` 로 주는 값을 C# `long`
  으로 읽으면 두 개가 하나로 합쳐진다. `int` 로 받는다.
- **`Rect`·`Size`·`Point` 는 Vortice 에도 있다.** 셸 파일에서는 `using Rect = Compositor_korean_win.Core.Rect;`
  같은 별칭을 붙인다.
- **레코드에 `Clone` 이라는 멤버를 둘 수 없다.** 컴파일러가 이미 쓴다.
- **`from` 은 쿼리 키워드다.** `from with { ... }` 는 LINQ 로 파싱돼 깨진다.
- **`stackalloc` 을 루프 안에 두지 말 것**(CA2014).
- 파일은 CRLF 로 체크아웃된다. 스크립트로 수정할 때 `newline=''` 로 읽고 `\r\n` 을 정규화한다.

## 6. 현재 위치

M0~M7 완료, 다음은 **M8(배포 — Releases 자동화, 업데이트 피드)**. M7의 두 배포판(AI 포함판/미포함판)은 CI 아티팩트까지만 만들어 둔 상태다.
C 커널은 전부 연결됐다. M3–M5가 미뤄 둔 UI 목록과 상세는 `docs/progress.md` 6.6절·7절.
