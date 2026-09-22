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
/// M0 bound only what it needed to prove the boundary works; M4 binds the rest — the magic wand,
/// spot healing and content-aware fill, which are the three places where a C loop is not an
/// optimisation but the difference between a tool and a hang.
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

    /// <summary>
    /// Marks every pixel within <paramref name="tolerance"/> of the one under the seed. Returns how
    /// many were selected, or -1 when memory runs out.
    /// </summary>
    /// <remarks>
    /// The kernel says <c>long</c>, which is 32 bits under the Windows data model however wide the
    /// machine is — so this is an <c>int</c> here, and the same goes for
    /// <see cref="HealCoverageBounds"/>'s four values. Declaring either as a 64-bit integer reads
    /// two of the kernel's numbers as one.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "wand_mask")]
    public static partial int WandMask(nint rgba, nuint width, nuint height, nuint stride,
                                        nuint seedX, nuint seedY, nuint radius, int tolerance,
                                        int contiguous, nint mask);

    /// <summary>
    /// Outlines a mask's nonzero pixels along pixel edges, as closed loops.
    /// </summary>
    /// <remarks>
    /// Outer boundaries come back clockwise and holes counterclockwise, which is exactly what the
    /// winding rule needs to fill the marked pixels and nothing else. Both output buffers are the
    /// kernel's to allocate and the caller's to <see cref="Free"/>.
    /// </remarks>
    [LibraryImport(Library, EntryPoint = "wand_trace")]
    public static partial int WandTrace(nint mask, nuint width, nuint height,
                                        out nint points, out nuint pointCount,
                                        out nint loops, out nuint loopCount);

    /// <summary>Half-open bounds of nonzero bytes in a grey bitmap, as four 32-bit values.</summary>
    [LibraryImport(Library, EntryPoint = "heal_coverage_bounds")]
    public static partial void HealCoverageBounds(nint gray, nuint width, nuint height, nuint stride, nint bounds);

    /// <summary>
    /// Spot healing in place: rebuilds what <paramref name="coverage"/> marks from the texture
    /// around it. Returns 0, or -1 when memory runs out.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "spot_heal")]
    public static partial int SpotHeal(nint rgba, nint coverage, nuint width, nuint height, nuint stride,
                                       float opacity, int mode, uint seed);

    /// <summary>
    /// Content-aware fill in place. Returns 1 on success, 0 when there is nothing to copy from,
    /// and -1 when memory runs out.
    /// </summary>
    [LibraryImport(Library, EntryPoint = "content_fill")]
    public static partial int ContentFill(nint rgba, nuint stride, nint mask, nuint maskStride,
                                          int width, int height);
}
