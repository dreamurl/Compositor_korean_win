using System.Runtime.InteropServices;

namespace Compositor_korean_win.Core;

/// <summary>
/// The upstream C pixel kernels, called directly.
/// </summary>
/// <remarks>
/// These eight files are the only upstream code this port reuses unchanged — they touch nothing
/// but stdint/stddef/math/string/stdlib, so MSVC compiles them as they are. <c>[LibraryImport]</c>
/// generates the marshalling at compile time, which keeps them callable from NativeAOT, where the
/// static library is linked straight into the executable and no <c>compositor_kernels.dll</c> ships.
/// Under the test run, which is plain CoreCLR, the same name resolves to the DLL instead.
///
/// M0 binds only what it needs to prove the boundary works. The rest arrive with the tools that
/// use them, in M4 and M5.
/// </remarks>
public static partial class Kernels
{
    private const string Library = "compositor_kernels";

    /// <summary>The kernel build's ABI stamp. Proves the native side is actually linked in.</summary>
    [LibraryImport(Library, EntryPoint = "kernels_abi_version")]
    public static partial int AbiVersion();

    /// <summary>Releases a buffer that a kernel allocated (<c>wand_trace</c>'s two outputs).</summary>
    [LibraryImport(Library, EntryPoint = "kernels_free")]
    public static partial void Free(nint pointer);

    /// <summary>
    /// Weighted per-channel histogram of premultiplied RGBA pixels. <paramref name="bins"/> is
    /// 4 × 256 doubles: combined first, then red, green and blue.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "levels_histogram")]
    public static partial void LevelsHistogram(nint pixels, nint coverage, nuint count, nint bins);

    /// <summary>Applies Levels in place; <paramref name="tables"/> is 3 × 256 floats.</summary>
    [LibraryImport(Library, EntryPoint = "levels_apply")]
    public static partial void LevelsApply(nint pixels, nuint count, nint tables);

    /// <summary>Adds noise in place, leaving alpha untouched.</summary>
    [LibraryImport(Library, EntryPoint = "noise_add")]
    public static partial void NoiseAdd(nint rgba, nuint width, nuint height, nuint stride,
                                        float amount, int gaussian, int monochromatic, uint seed);

    /// <summary>Half-open bounds of nonzero alpha, as left, top, right, bottom.</summary>
    [LibraryImport(Library, EntryPoint = "brush_alpha_bounds")]
    public static partial void BrushAlphaBounds(nint bytes, nuint width, nuint height, nuint stride, nint bounds);

    /// <summary>Copies the alpha channel out into an 8-bit grey bitmap.</summary>
    [LibraryImport(Library, EntryPoint = "layer_extract_alpha")]
    public static partial void LayerExtractAlpha(nint rgba, nuint rgbaStride,
                                                 nint gray, nuint grayStride, nuint width, nuint height);

    /// <summary>
    /// Divides the alpha back out of every pixel and sets alpha to full.
    /// </summary>
    /// <remarks>
    /// Half of a clipping group: making the base opaque is what lets the layers clipped to it
    /// composite at full strength instead of half-covering it. Pair it with
    /// <see cref="LayerRestoreAlpha"/>.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "layer_unpremultiply_opaque")]
    public static partial void LayerUnpremultiplyOpaque(nint rgba, nuint stride, nuint width, nuint height);

    /// <summary>Puts a saved alpha channel back, premultiplying the colour by it again.</summary>
    [LibraryImport(Library, EntryPoint = "layer_restore_alpha")]
    public static partial void LayerRestoreAlpha(nint rgba, nuint stride,
                                                 nint alpha, nuint alphaStride, nuint width, nuint height);
}
