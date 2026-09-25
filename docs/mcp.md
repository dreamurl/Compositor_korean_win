# AI가 이 편집기를 쓰게 하기

AI(Claude, Codex 등)가 이 편집기의 기능을 도구로 불러서 레이어 문서를 만들고, 결과를 이미지로 보고,
PSD·프로젝트·PNG로 저장할 수 있다. 방법은 두 가지다.

- **등록 없이, 열린 창에서 (권장)** — Compositor를 켜 두고 AI에게 안내문을 붙여넣기만 하면 된다.
  AI의 작업이 화면에 바로 나타나고, 한 번의 도구 호출이 실행 취소 한 단계가 된다. 0절.
- **MCP 서버로 등록** — AI 도구의 설정에 등록해 두고 쓴다. 1절. 창이 열려 있으면 이 경우에도
  작업이 그 창으로 전달된다.

설계와 검증 기록은 [`progress.md`](progress.md) 15절(MCP)과 25절(열린 창 연결)에 있다.

## 0. 등록 없이 쓰기 — 열린 창에 직접

설치 프로그램으로 설치했다면, 터미널을 쓸 수 있는 AI(Claude Code, Codex 등)에게 이렇게만 말하면 된다.

> 컴포지터 켜 놨어. `compositor` 명령어로 연결해서 작업해 줘.

AI가 `compositor`를 실행하면 가이드(작업 규칙과 모든 도구)가 나오고, 이후 작업도 같은 명령으로 보낸다.
Compositor가 꺼져 있으면 명령이 알아서 켠다. 설치 프로그램이 설치 폴더를 사용자 PATH에 등록하므로 경로를
말할 필요가 없다(제거하면 PATH에서도 빠진다). 설치 직후 이미 열려 있던 터미널·AI 앱은 PATH 변경을 보지 못하니
새로 연다.

```
compositor                                   # 가이드
compositor --version                         # 앱에 연결하지 않고 설치 버전 확인
compositor get_document
compositor add_text text="안녕하세요" x=100 y=200 size=72 color=#E02020
compositor edit_text layer=제목 start=0 end=2 size=120
compositor render                            # image: <PNG 경로>
```

값은 `key=value`다. JSON 숫자 형식인 값과 `true`/`false`는 그 형식으로, `[..]`·`{..}`는 JSON으로, 나머지는
글자로 넘어간다. 숫자는 쓴 그대로 보내므로 `layer=007`은 `7`이 되지 않고 "007"로 도착한다(`.5`·`+3`처럼 JSON이
아닌 숫자는 글자로 가고, 도구가 숫자가 필요한 자리에서는 숫자로 읽는다). `text`와 `prompt`는 형식을 판별하지 않고
항상 글자로 보낸다(`text=2024`, `text=[SALE]`도 글자). 그 안의 `\n`은 줄바꿈, `\\`는 역슬래시 하나로 **편집기가**
읽는다(`text=a\\nb` → `a\nb` 글자 그대로). 명령이 아니라 편집기가 읽으므로 MCP·파이프로 JSON을 보내는 AI에게도
같은 규칙이다. 다른 인수의 역슬래시는 그대로 두므로 `path=C:\new\poster.psd` 같은 윈도우 경로가 깨지지 않는다.
PowerShell에서 쉼표가 든 값(`points=[[10,20],[30,40]]`)은 따옴표로 감싼다 — 감싸지 않으면 PowerShell이 쉼표에서
인수를 나눈다.

종료 코드는 도구 실패 1, 인수 오류 2, 편집기 시작 실패 3, 권한 거부 4, 연결 실패 5, 응답 시간 초과 6이다.
실행 중인 Compositor 프로세스가 있으면 연결에 실패해도 새 창을 열지 않는다.

사람이 드래그·붓질·텍스트 입력 중이거나 대화상자를 열어 두면 요청은 끝날 때까지 기다린다. 그래서 명령은 답을
**최대 5분** 기다리고, 10초가 넘으면 기다리는 이유를 오류 출력에 한 번 알린다. 시간은 환경변수
`COMPOSITOR_TIMEOUT`(초, `0`은 무제한)으로 바꾼다. 시간 초과로 명령이 끝나면 창은 아직 시작하지 않은 그 요청을
**철회**한다 — 나중에 몰래 적용돼서 AI가 다시 보낸 요청과 겹치는 일이 없다. 이미 실행 중이던 요청은 끝까지
실행되므로, 시간 초과 뒤에는 다시 보내기 전에 `get_document`로 확인한다.

설치하지 않은 휴대용(zip)이나 명령을 못 찾는 경우에는 메뉴 **도움말 › AI 작업 안내 복사**로 받은 안내문을
AI에게 붙여넣으면 된다. 안내문에는 명령과, 명령이 없을 때 쓰는 PowerShell 함수가 함께 들어 있다.

**어떻게 연결되나.** 열린 창은 현재 사용자만 접근할 수 있는 로컬 통로
`\\.\pipe\compositor-korean-win`(윈도우 named pipe)을 연다. AI는 안내문의 PowerShell 함수로 이 통로에
JSON 한 줄을 보내고 한 줄을 받는다. 마우스·키보드를 쓰지 않으며, 사람이 드래그·붓질·텍스트 입력 중일
때는 끝날 때까지 기다렸다가 적용한다.

```powershell
function Compositor([string]$json) { ... }   # 안내문에 들어 있는 함수
Compositor '{"tool":"guide"}'                 # 사용법과 전체 도구 목록
Compositor '{"tool":"new_document","arguments":{"width":1080,"height":1350}}'
Compositor '{"tool":"render"}'                # 결과를 PNG 파일로 저장하고 경로를 알려 준다
```

답은 `{"ok": true|false, "text": "...", "images": ["...png"]}` 형태다. 도구는 창에 보이는 문서에
작동하고, `new_document`·`open_document`는 새 탭을 연다. `undo`·`redo`는 창의 실행 취소 기록을 쓴다.
Compositor는 한 번만 실행된다. 다시 실행하면 기존 창을 앞으로 가져오며, 명령은 그 창의 통로를 쓴다.

### 연결 문제 해결

- Compositor와 `compositor` 명령은 같은 Windows 사용자와 권한 수준으로 실행한다.
- Codex 같은 샌드박스에서는 현재 사용자 전용 named pipe 접근 승인이 필요할 수 있다. 보안을 위해
  `CurrentUserOnly` 제한을 제거하지 않는다.
- 앱이 실행 중인데 연결되지 않으면 CLI는 새 창을 만들지 않고 권한 거부, 연결 실패 또는 응답 시간 초과를
  구분해서 출력한다. 메시지와 종료 코드를 확인한 뒤 권한을 승인하거나 앱을 다시 시작한다.

### 작업 규칙

AI가 따를 작업 규칙(레이어를 역할별 그룹으로 나누기, 의미 있는 이름, 텍스트는 래스터화하지 않기,
사용자 레이어는 지우기 전에 묻기 등)은 `%APPDATA%\Compositor_korean_win\ai-rules.md`에 있다.

- 처음 실행할 때 기본 규칙으로 만들어진다. 지우면 다음 실행 때 기본값으로 다시 만들어진다.
- 메뉴 **도움말 › AI 작업 규칙 편집**으로 메모장에서 열어 고친다. 요청마다 다시 읽으므로 저장하면 바로 적용된다.
- 통로로 오는 요청은 `guide`를 읽기 전에는 거절된다. 규칙을 고친 뒤에도 다시 읽어야 한다. 그래서 AI는
  규칙을 읽지 않고는 작업할 수 없다. MCP로 연결한 AI는 연결할 때 받는 안내(instructions)로 규칙을 받는다.
- 제거 프로그램은 이 폴더를 지우므로, 고친 규칙을 남기려면 제거 전에 따로 보관한다.

## 1. 등록하기

아래 경로는 설치 위치에 맞게 바꾼다. 사용자별 설치의 기본 위치는
`%LOCALAPPDATA%\Programs\Compositor_korean_win\Compositor_korean_win.exe`다.
배경 제거(`remove_background`)는 **AI 포함판**에서만 된다.

**Claude Code**

```powershell
claude mcp add compositor -- "C:\Users\<사용자>\AppData\Local\Programs\Compositor_korean_win\Compositor_korean_win.exe" --mcp
```

**Codex CLI** — `%USERPROFILE%\.codex\config.toml`

```toml
[mcp_servers.compositor]
command = 'C:\Users\<사용자>\AppData\Local\Programs\Compositor_korean_win\Compositor_korean_win.exe'
args = ["--mcp"]
```

**Claude Desktop** — `claude_desktop_config.json`

```json
{
  "mcpServers": {
    "compositor": {
      "command": "C:\\Users\\<사용자>\\AppData\\Local\\Programs\\Compositor_korean_win\\Compositor_korean_win.exe",
      "args": ["--mcp"]
    }
  }
}
```

등록한 뒤 AI에게 "compositor로 1080×1350 포스터를 만들어 줘"처럼 요청하면 된다. 작업이 끝나면 AI가 저장한
`.psd`나 `.comp`를 편집기에서 열어 이어서 손볼 수 있다.

## 2. 도구

좌표는 문서 왼쪽 위 기준 픽셀, 색은 `#RRGGBB`(`#RRGGBBAA`, `"transparent"`), 불투명도는 0–1이다.
레이어를 만드는 도구는 레이어 id를 돌려주고, 다른 도구는 id나 이름으로 레이어를 가리킨다.

| 묶음 | 도구 | 하는 일 |
|---|---|---|
| 문서 | `new_document`, `open_document`, `save_document`, `export_image` | 새로 만들기, `.comp`·`.psd`·`.psb`·이미지 열기, `.psd`·`.comp` 저장, PNG·JPEG 내보내기 |
| | `list_documents`, `select_document`, `close_document`, `resize_canvas` | 여러 문서 오가기, 캔버스 크기 |
| 보기 | `get_document`, `render` | 레이어 목록(JSON), 문서·레이어·영역을 PNG로 보기 |
| 기록 | `undo`, `redo` | 편집기와 같은 실행 취소 기록 |
| 레이어 만들기 | `add_layer`, `add_image`, `add_text`, `add_shape`, `add_gradient`, `add_adjustment`, `generate_image` | 빈 레이어, 사진 배치, 살아 있는 텍스트(뒤틀기 15종), 사각형·타원·다각형·별·선, 그라디언트, 조정 레이어 6종, 이미지 생성 |
| 레이어 다루기 | `update_layer`, `arrange_layer`, `delete_layers`, `duplicate_layer`, `group_layers`, `ungroup`, `merge_layers`, `rasterize_layer` | 이름·표시·불투명도·혼합 모드·위치·크기·회전·뒤집기, 순서, 그룹 |
| 합성 | `set_clipping`, `set_mask`, `set_effects` | 클리핑, 마스크(사각형·타원·선형·원형 그라디언트·선택 영역·이미지, 페더, 반전), 드롭 섀도·외부 광선·획 |
| 조정 | `add_adjustment`, `edit_adjustment` | 조정 레이어 6종. 레벨·곡선은 채널별(`red`/`green`/`blue`, `red_points`…), 색조/채도는 색 범위별(`ranges`). 나중에 값 일부만 바꾸기 |
| 선택 | `select`, `modify_selection` | 사각형·타원·올가미(점 목록)·레이어 픽셀·마술봉·전체·해제, 더하기·빼기·교차, 반전·확장·축소·크기·회전·이동, 페더 |
| 픽셀 | `apply_filter`, `fill_selection`, `copy_to_layer`, `remove_background` | 흐림·노이즈·렌즈 보정·핀치·구형화·돌리기·물결·극좌표·반전, 조정을 픽셀에 굽기, 선택 영역 채우기·지우기·내용 인식 채우기, 선택 영역을 새 레이어로(복사·잘라내기), AI 배경 제거 |
| 손 도구 | `paint_stroke`, `liquify`, `warp_layer`, `distort_layer` | 점 목록으로 브러시·지우개·복제 도장·스팟 힐링·흐림(마스크에도), 유동화 8종, 4×4 뒤틀기(프리셋·점), 네 모서리 왜곡·원근 |
| 묶음 | `batch` | 여러 호출을 한 번에: 실행 취소 한 단계, 하나라도 실패하면 전부 되돌림 |
| 기타 | `edit_text`, `list_fonts` | 글자·스타일 바꾸기(`start`/`end`로 일부 글자만), 설치된 글꼴 |

손 도구와 선택은 사람이 마우스로 하는 동작을 좌표로 한다.

- 점과 크기는 모두 문서 픽셀이다. 옮기거나 크기를 바꾸거나 돌린 레이어에도 `render`에서 보이는 자리에 칠해진다.
- 선택 영역은 `paint_stroke`, `fill_selection`, `copy_to_layer`, `apply_filter`, `set_mask shape=selection`이 따른다.
  열린 창에서는 사람의 선택 영역이 곧 AI의 선택 영역이고, AI가 만든 선택 영역은 창에 개미 행렬로 보인다.
  페더는 편집기 선택 영역에는 없는 값이라 AI 쪽에만 있고, 사람이 선택을 바꾸면 0으로 돌아간다.
- 텍스트 레이어에 칠하거나 유동화·뒤틀기·왜곡을 하면 픽셀이 된다. 살아 있는 텍스트를 휘게 하려면 `edit_text`의 `warp`를 쓴다.
- `render`의 `zoom`으로 영역을 픽셀 그대로 확대해 볼 수 있고(`zoom=4`면 한 픽셀이 4×4), `layers`·`hide`로
  몇 레이어만 보거나 숨겨서 본다.
- `batch`는 `{"calls":[{"tool":…,"arguments":{…}}, …]}`를 받는다. 창에서는 한 번의 실행 취소로 전부 되돌아간다.
  `undo`·`redo`·`close_document`·`batch`는 넣을 수 없다.

## 3. 이미지 생성 (`generate_image`)

Claude는 사진을 그리지 못하므로, 인물 사진 같은 것은 이미지 생성기를 불러 만든다. 아래 순서로 찾아 쓴다.

1. `COMPOSITOR_IMAGE_COMMAND` — PNG를 `{output}`에 쓰는 아무 명령. `{prompt}`, `{output}`, `{width}`, `{height}`가 채워진다.
2. `OPENAI_API_KEY` — OpenAI 이미지 API를 직접 부른다. 모델은 `COMPOSITOR_IMAGE_MODEL`(기본 `gpt-image-2`).
3. `PATH`의 `codex` — 로그인된 Codex CLI에 `codex exec`로 `$imagegen`을 써서 지정한 경로에 PNG를 저장하라고 시킨다.

`COMPOSITOR_IMAGE_GENERATOR`(`command`, `openai`, `codex`)로 하나를 강제할 수 있다. 셋 다 없으면 도구가 설정 방법을
알려 주고, AI는 다른 곳에서 만든 이미지를 `add_image`로 넣으면 된다. 이런 환경 변수는 MCP 클라이언트 설정의
`env`에 넣는다(예: Codex의 `[mcp_servers.compositor.env]`).

## 4. 동작 방식

- 서버는 편집기 코어(`Core/Automation`)를 그대로 쓴다. 도구마다 편집기의 명령을 그대로 부르므로, AI가 만든 결과는
  사람이 같은 설정으로 만든 것과 같고 편집기에서 편집 가능한 레이어로 열린다.
- 편집은 편집기와 같은 실행 취소 기록(`DocumentHistory`, 픽셀 소유)을 거친다. 되돌린 픽셀은 해제된다.
- 창이나 GPU가 필요 없다. 렌더링은 소프트웨어 렌더러, 글꼴은 DirectWrite, 이미지 해독은 WIC를 쓴다.
- 메시지는 한 줄에 하나인 JSON-RPC 2.0이고 UTF-8이다. 로그는 표준 오류로 나간다.
