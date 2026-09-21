# 제3자 고지

이 저장소가 배포하는 모든 산출물에 아래 고지가 함께 나가야 한다.
docs/windows-port.md 9절이 이를 "상시" 항목으로 두고 있다.

---

## Compositor (Wonder Assembly LLC)

이 프로젝트는 [Compositor](https://github.com/robbietilton/Compositor) 1.0.4를 바탕으로 한다.
알고리즘·문서 모델·`.comp` 파일 포맷·테스트 설계를 가져왔고,
`native/kernels/` 의 C 파일 여덟 개(`AdjustPixels.c`·`BrushPixels.c`·`ContentFill.c`·
`HealPixels.c`·`LensPixels.c`·`LevelsPixels.c`·`NoisePixels.c`·`WandPixels.c`)와
그 헤더는 **한 줄도 고치지 않고 그대로** 쓰고 있다.

```
MIT License

Copyright (c) 2026 Wonder Assembly LLC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

`native/kernels/shim/KernelsShim.c` 와 `native/kernels/kernels.def` 는 원본에 없는
이 프로젝트의 추가분이며, 원본 파일을 손대지 않고 갱신할 수 있도록 분리해 두었다.

---

## Vortice.Windows

Direct2D·Direct3D 11·DXGI·WIC 의 COM 인터페이스 바인딩.
DLL 자체는 윈도우에 내장돼 있으므로 번들되는 것은 바인딩뿐이다.

```
MIT License

Copyright (c) 2019-2026 Amer Koleci and contributors
```

SharpGen.Runtime(같은 저작자, MIT)을 함께 사용한다.

---

## 상표

"Adobe" 와 "Photoshop" 은 Adobe Inc. 의 상표다.
이 프로젝트는 Adobe 와 무관하며, 제품명·저장소명·배포물 어디에도 그 이름을 쓰지 않는다.
단축키와 용어가 Photoshop 관례를 따르는 것은 호환을 위한 것이지 제휴를 뜻하지 않는다.
