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
compositor get_document
compositor add_text text="안녕하세요" x=100 y=200 size=72 color=#E02020
compositor edit_text layer=제목 start=0 end=2 size=120
compositor render                            # image: <PNG 경로>
```

값은 `key=value`다. 숫자·`true`/`false`는 그 형식으로, `[..]`·`{..}`는 JSON으로, 나머지는 글자로 넘어간다.
도구가 실패하면 이유를 출력하고 종료 코드 1로 끝난다.

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
창이 두 개 열려 있으면 먼저 연 창이 통로를 갖는다.

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
| 합성 | `set_clipping`, `set_mask`, `set_effects` | 클리핑, 마스크(사각형·타원·선형·원형 그라디언트), 드롭 섀도·외부 광선·획 |
| 픽셀 | `apply_filter`, `remove_background` | 가우시안·동작 흐림, 노이즈, AI 배경 제거 |
| 기타 | `edit_text`, `list_fonts` | 글자·스타일 바꾸기(`start`/`end`로 일부 글자만), 설치된 글꼴 |

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
