# 윈도우 이식 설계 — Compositor 한국어 윈도우판

원본 [Compositor](https://github.com/robbietilton/Compositor) 1.0.4(macOS 전용, MIT)를
윈도우용 한국어 이미지 편집기로 다시 구현하기 위한 아키텍처 설계와 단계별 계획이다.

**전제**: 로컬 빌드는 하지 않는다. 모든 컴파일·테스트·패키징은 GitHub Actions에서 수행한다.

---

## 1. 왜 "이식"이 아니라 "재구현"인가

원본 Swift 24,018줄 중 UI·렌더링·IO는 Apple 전용 프레임워크에 직결돼 있어
윈도우로 옮길 수 없다. `import` 분포가 이를 그대로 보여준다.

| 프레임워크 | 사용 횟수 | 윈도우 |
|---|---|---|
| AppKit | 94 | 없음 |
| SwiftUI | 37 | 없음 |
| CoreGraphics | 21 | 없음 |
| CoreImage | 11 | 없음 |
| ImageIO | 9 | 없음 |
| Accelerate(vImage) | 2 | 없음 |
| Vision | 1 | 없음 |
| Metal | 1 | 없음 |

데이터 모델조차 `CGImage`(281회)·`CGContext`(79회)에 묶여 있어,
Document 계층 8,071줄도 그대로는 쓸 수 없다.

**그래서 옮기는 것은 코드가 아니라 설계다.** 알고리즘·문서 모델·파일 포맷·테스트를 옮기고,
플랫폼에 닿는 표면은 새로 쓴다.

---

## 2. 반드시 계승해야 할 것 — 경량성의 실제 출처

원본 배포본은 **DMG 7.4MB**다. 이 수치는 macOS가 무거운 부분을 OS로 제공하기 때문에 나온 것이고,
동시에 원본이 1억 픽셀 문서를 다루면서도 가벼운 이유는 네 가지 구조적 장치에 있다.
**이 네 가지는 스택과 무관하게 그대로 가져간다.**

### 2.1 비파괴 변환

이동·확대·회전·뒤집기는 픽셀을 재생성하지 않고 레이어 메타데이터(`origin`·`size`·`rotation`·`flip`·`sampling`)로만 남는다.
아무리 작게 줄여도 원본 해상도가 보존된다. (`docs/project-format.md`: "transforms remain separate")

### 2.2 불변 픽셀 자산 + 히스토리 참조 공유

`DocumentHistory`는 값 스냅샷만 쌓고 픽셀은 복사하지 않는다 — 원본 주석 그대로
"Value snapshots share immutable CGImages; no pixel copies for layer edits".
실행취소 100단계, 리테인 상한 256MiB로 관리한다.
**메모리 사용량을 가장 크게 좌우하는 장치이므로 윈도우판에서도 픽셀 버퍼는 불변·공유·참조 계수로 다룬다.**

### 2.3 타일 교체 + 다운샘플 피라미드

`TiledLayerRenderer`는 편집 중인 영역만 타일로 교체해 그리고,
`DownsampleCache`는 축소 표시용으로 미리 절반씩 줄인 단계(sharp halvings)를 캐시해
마지막 2× 이하만 실시간 리샘플한다. 전체 재합성을 피하는 핵심이다.

### 2.4 지연 할당

마스크는 칠하기 전까지 1×1 균일 버퍼로 둔다. 전체 해상도 할당을 미룬다.

### 2.5 윈도우에서 추가로 지켜야 할 것

- **렌더러를 번들하지 않는다.** Skia(약 10MB)나 Chromium(약 150MB)을 포함하는 순간 위 장점이 사라진다.
  OS에 있는 Direct2D를 쓴다.
- **AI 모델을 번들하지 않는다.** 7절 참조.

---

## 3. 프레임워크 대응

윈도우는 macOS의 이미지 스택과 거의 1:1로 대응하는 OS 내장 구성요소를 갖고 있다.

| 원본(macOS) | 윈도우 대응 | 비고 |
|---|---|---|
| CoreGraphics `CGContext` | Direct2D `ID2D1DeviceContext` | GPU 가속 |
| CoreGraphics `CGImage` | `ID2D1Bitmap1` + 자체 픽셀 버퍼 | 4절 `PixelBuffer` |
| `CIColorMatrix` | `D2D1ColorMatrix` | |
| `CIColorCube` | `D2D1LookupTable3D` | |
| `CIGaussianBlur` | `D2D1GaussianBlur` | |
| `CIMotionBlur` | `D2D1DirectionalBlur` | |
| `CIPerspectiveTransform` | `D2D13DPerspectiveTransform` | |
| `CIBlendWithMask` | `D2D1AlphaMask` / `D2D1Composite` | |
| 블렌드 모드 9종 | `D2D1Blend` 효과 | Multiply·Screen·Overlay·Darken·Lighten·Difference·ColorDodge·ColorBurn 전부 내장 |
| Metal 브러시 커버리지 | D3D11 컴퓨트 셰이더 | CPU 폴백 우선 구현 |
| Vision 피사체 분리 | ONNX Runtime + DirectML | 7절 |
| ImageIO | WIC (Windows Imaging Component) | JPEG·PNG·TIFF·HEIC |
| Accelerate(vImage) | 자체 C 구현 또는 D2D 효과 | 사용처 2곳뿐 |
| Sparkle 자동 업데이트 | 자체 업데이터 + GitHub Releases | 8절 |

**대체 불가 지점은 없다.** 원본이 CoreImage에서 실제로 쓰는 필터는 6종뿐이고 전부 대응된다.

---

## 4. 계층 구조

```
┌─────────────────────────────────────────────┐
│  Shell (UI)          창·패널·시트·입력·IME   │  <- 교체 가능
├─────────────────────────────────────────────┤
│  Render Backend      Direct2D / WIC / D3D11 │  <- 교체 가능
├─────────────────────────────────────────────┤
│  Core (플랫폼 무관)                          │
│   Document  레이어 트리·변환·마스크·선택     │
│   History   불변 자산 공유 스냅샷            │
│   Format    .comp v1~6 읽기/쓰기             │
│   Kernels   C 879줄 (P/Invoke)              │
├─────────────────────────────────────────────┤
│  AI Module (지연 로드, 선택적)               │  <- 분리 배포
└─────────────────────────────────────────────┘
```

### 4.1 Core — 플랫폼 무관 계층

`System.*`와 C 커널 외에 아무것도 참조하지 않는다. 테스트는 전부 여기에 붙는다.

**`PixelBuffer`가 설계의 중심이다.** 원본이 `CGImage`에 직접 의존한 자리를 대신한다.

```csharp
// 불변. 생성 후 픽셀이 바뀌지 않으므로 히스토리·스레드 간 자유롭게 공유한다.
sealed class PixelBuffer {
    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    // premultiplied RGBA8888, unmanaged 메모리 — GC 압박을 받지 않는다
    public IntPtr Scan0 { get; }
}
```

- **unmanaged 할당**: 1억 픽셀 = 400MB. 관리 힙에 두면 LOH 단편화와 GC 정지가 생긴다.
- **참조 계수**: 히스토리가 같은 버퍼를 공유하므로 마지막 참조가 사라질 때 해제한다.
- **불변**: 편집은 항상 새 버퍼를 만들거나 타일 패치를 덧붙인다(2.2·2.3 계승).

### 4.2 Render Backend — 교체 가능한 인터페이스

원본이 이미 `LayerRenderer`/`TiledLayerRenderer`로 분리해 둔 경계를 그대로 유지한다.

```csharp
interface IRenderBackend {
    void DrawLayer(PixelBuffer image, LayerTransform t, Point center,
                   double scale, double opacity, BlendMode blend, PixelBuffer? mask);
    PixelBuffer ApplyEffect(EffectDescriptor effect, PixelBuffer source);
    PixelBuffer Downsample(PixelBuffer source, int level);
}
```

1차 구현은 `Direct2DBackend`. 이 경계를 지키면 나중에 백엔드를 갈아끼울 수 있고,
Core 테스트는 백엔드 없이(소프트웨어 래스터라이저로) 돌릴 수 있다.

### 4.3 C 커널 — 유일하게 그대로 재사용되는 코드

879줄 8파일이 `stdint`·`stddef`·`math`·`string`·`stdlib`만 쓴다. **플랫폼 의존이 0이다.**

| 파일 | 줄 | 역할 |
|---|---|---|
| `HealPixels.c` | 259 | 스팟 힐링(content-aware) |
| `WandPixels.c` | 174 | 마술봉 |
| `AdjustPixels.c` | 96 | 조정 |
| `ContentFill.c` | 93 | Content-Aware Fill |
| `BrushPixels.c` | 50 | 브러시 알파 경계·프리멀티플 |
| `NoisePixels.c` | 41 | 노이즈 추가 |
| `LensPixels.c` | 37 | 렌즈 보정 |
| `LevelsPixels.c` | 28 | 레벨·히스토그램 |

시그니처가 포인터+크기 형태라 P/Invoke가 자명하다.

```csharp
[LibraryImport("compositor_kernels")]
internal static partial int content_fill(IntPtr rgba, nuint stride,
                                         IntPtr mask, nuint maskStride, int width, int height);
```

`LibraryImport`(소스 생성기)를 쓰면 NativeAOT에서도 리플렉션 없이 동작한다.
MSVC로 컴파일해 NativeAOT 바이너리에 **정적 링크**하면 별도 DLL도 필요 없다.

### 4.4 파일 포맷 — 그대로 구현한다

`.comp`는 이미 플랫폼 중립이다. `manifest.json` + `images/<UUID>.png` 구조이고
v1~6 스키마가 `docs/project-format.md`에 전부 문서화돼 있다.

**그대로 구현하면 macOS판과 파일이 오간다.** 이는 포기하기 아까운 자산이므로
v6 전체(그룹·불투명도·블렌드·레이어 마스크·폴더 마스크·클리핑 마스크)를 읽고 쓰며,
원본의 검증 규칙(순환 참조·64단계 중첩 한도·256노드 체인 한도·크기 상한)도 같이 옮긴다.

macOS의 document package(디렉터리)를 윈도우에서는 **단일 zip 컨테이너**로 다룬다.
윈도우 탐색기에는 디렉터리 패키지 개념이 없어 폴더가 그대로 노출되면 사용성이 나쁘다.
→ **미결 사항**: 그러면 macOS판과 바이너리 호환이 깨진다. 9절 참조.

---

## 5. 기술 스택

| 계층 | 선택 | 근거 |
|---|---|---|
| 언어/런타임 | C# / .NET 9 **NativeAOT** | 단일 exe, 런타임 설치 불필요, 기동 즉시, GC 최소화 |
| 렌더링 | Direct2D + D3D11 (Vortice.Windows) | OS 내장 — 번들 증가 없음, GPU 가속, CoreImage 효과와 1:1 |
| 이미지 IO | WIC | OS 내장 |
| 픽셀 커널 | 기존 C 879줄, MSVC 정적 링크 | 무수정 재사용 |
| AI | ONNX Runtime + DirectML EP | 온디맨드 로드 |
| UI 셸 | **M0에서 실측 후 확정** | 아래 |

### 5.1 UI 셸이 미확정인 이유

가벼움을 최우선으로 두면 선택이 갈린다. M0에서 실측해 정한다.

| 후보 | 추가 용량 | 장점 | 단점 |
|---|---|---|---|
| Win32 + Direct2D 자체 위젯 | 0 | 가장 가볍고 캔버스와 렌더 경로 통일 | 슬라이더·패널·드래그앤드롭을 직접 구현 |
| WinUI 3 (SwapChainPanel) | 약 40MB(self-contained) | 캔버스 D3D 직결, 생산성 | Windows App SDK 배포 부담, 용량 |
| Avalonia | 약 20MB(Skia 포함) | 안정적, 장기적으로 macOS 통합 여지 | Skia를 번들하게 됨 |

**Win32 + Direct2D 자체 위젯이 유력하다.** 근거가 하나 더 있다 —
원본에는 **텍스트 도구가 없다**. 한글 입력이 필요한 지점은 레이어 이름 인라인 편집과
수치 입력창뿐이라, 그 자리에만 네이티브 EDIT 컨트롤을 띄우면 IME 부담이 사실상 사라진다.
자체 위젯의 가장 큰 위험 요소가 제거되는 셈이다.

---

## 6. 한국어화

원본에는 `.lproj`도 String Catalog도 없고 UI 문자열 약 187개가 소스에 하드코딩돼 있다.
**어차피 새로 쓰므로 처음부터 리소스로 분리한다.**

- `.resx` 기반, 기본 `ko-KR`, `en` 병행 → 원본보다 상위 호환
- 용어는 한국어판 Photoshop 관례를 따른다(레이어·마스크·클리핑 마스크·불투명도·혼합 모드)
- **단축키 매핑표가 별도로 필요하다**: ⌘→Ctrl, ⌥→Alt, ⌃→Ctrl 충돌 해소.
  원본은 "Photoshop-style keyboard shortcuts throughout"이므로 윈도우판 Photoshop 관례에 맞춘다.

---

## 7. AI 도입 설계

나중에 얹을 것을 전제로 **지금 구조만 잡아둔다.**

- **모델을 번들에 넣지 않는다.** 배경 제거용 RMBG-1.4만 해도 약 44MB다.
  기본 앱은 가볍게 두고, 기능을 처음 쓸 때 모델을 내려받아 사용자 데이터 폴더에 캐시한다.
- **추론은 DirectML EP로 GPU에 넘긴다.** 공급업체 중립이라 NVIDIA·AMD·Intel 모두 동작한다.
- **모듈 경계**: `IAiProvider`를 두고 AI 미설치 상태에서도 앱 전체가 정상 동작해야 한다.
- 1차 대상은 원본의 Vision 사용처인 피사체 분리(`Compositor/Document/SubjectRemoval.swift`, 110줄) 대체.
- 확장 여지: 생성형 채우기, 업스케일, 노이즈 제거 — 전부 같은 온디맨드 모델 구조를 탄다.

---

## 8. CI / 배포

로컬 빌드를 하지 않으므로 **Actions가 유일한 빌드 환경**이다.

```
windows-latest
 ├─ MSVC로 C 커널 컴파일 → 정적 라이브러리
 ├─ dotnet publish -r win-x64 -p:PublishAot=true
 ├─ 테스트 실행 (Core 계층, 백엔드 없이)
 └─ 태그 푸시 시 단일 exe를 GitHub Releases에 업로드
```

- **자동 업데이트**: Sparkle 대체가 필요하다. `appcast.xml`과 같은 역할의 JSON 피드를
  GitHub Releases에 두고 앱이 폴링하는 최소 구현으로 충분하다.
- **코드 서명은 미해결 비용이다.** 서명 없는 exe는 SmartScreen 경고가 뜬다.
  무료 배포이므로 Azure Trusted Signing(월 단위 과금) 채택 여부를 별도로 판단해야 한다.
  당분간은 미서명 배포 + 설치 안내로 시작한다.

---

## 9. 리스크와 미결 사항

| 항목 | 내용 | 판단 시점 |
|---|---|---|
| UI 셸 | 3개 후보 중 실측으로 확정 | M0 |
| 픽셀 채널 순서 | C 커널은 RGBA 전제, Direct2D 관례는 BGRA. `DXGI_FORMAT_R8G8B8A8_UNORM` 지원을 확인해 변환 비용을 없앨 것 | M0 |
| `.comp` 컨테이너 | 디렉터리 패키지 → zip 전환 시 macOS판과 호환이 깨짐. 양쪽 읽기를 지원할지 결정 필요 | M1 |
| 업스트림 추적 | 원본이 계속 개선되면 알고리즘 변경을 어떻게 따라갈지 | M1 |
| 앱 이름 | 원저작물과의 혼동을 피하려면 `Compositor`를 그대로 쓰지 않는 편이 낫다. **"Photoshop"은 상표이므로 제품명·저장소명에 쓰지 않는다** | M0 |
| 라이선스 | MIT. 원 저작권 고지(Wonder Assembly LLC)를 배포물에 반드시 포함 | 상시 |
| 코드 서명 | SmartScreen 경고 | M8 |

---

## 10. 로드맵

각 단계는 Actions에서 초록불이 켜지는 것으로 완료를 확인한다.

| 단계 | 내용 | 완료 기준 |
|---|---|---|
| **M0** 기반 검증 | Win32+D2D 창에 PNG 한 장 표시, C 커널 하나 P/Invoke 호출, NativeAOT 빌드 | **실측치**: 번들 크기·기동 시간·메모리. UI 셸과 픽셀 포맷 확정 |
| **M1** 문서 코어 | `.comp` v1~6 리더/라이터, 레이어 트리, 변환, 불변 히스토리 | 원본 `CompositorTests` 이식분이 통과 |
| **M2** 렌더 백엔드 | `IRenderBackend` + Direct2D 구현, 블렌드 9종, 마스크, 클리핑, 다운샘플 피라미드, 타일 교체 | 참조 이미지와 픽셀 비교 통과 |
| **M3** 캔버스 | 뷰포트·줌·팬·변환 핸들·스냅·가이드·선택 영역 | 1억 픽셀 문서에서 60fps 유지 |
| **M4** 도구 | 브러시·힐링·클론·블러·그라디언트·셰이프·마술봉 (C 커널 연결) | 스트로크 지연 측정 |
| **M5** 조정·필터 | Levels·Curves·HueSat·Exposure·GradientMap·Grain·블러 (D2D 효과) | 라이브 프리뷰 동작 |
| **M6** UI 완성 | 레이어 패널·탭·시트 + 한국어 리소스 + 단축키 | 전 기능 한국어 |
| **M7** AI | 피사체 분리(ONNX+DirectML, 온디맨드) | 모델 미설치 시에도 앱 정상 |
| **M8** 배포 | 단일 exe, 업데이트 피드, Releases 자동화 | 다운로드→실행 검증 |

M1과 M2가 전체 비용의 대부분을 차지한다. M0의 실측 결과에 따라 이후 설계가 조정될 수 있다.
