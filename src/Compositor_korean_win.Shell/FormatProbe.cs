using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Answers the channel-order question docs/windows-port.md §9 hands to M0.
/// </summary>
/// <remarks>
/// The eight C kernels are written against RGBA, while Direct2D is conventionally fed BGRA. If
/// <see cref="Format.R8G8B8A8_UNorm"/> works as a render target and as a swap chain format, the
/// whole pipeline can stay RGBA and no channel swap is needed anywhere — which matters because a
/// swap on a 100-megapixel document is 400 MB of pointless traffic per pass. If it does not, the
/// swap has to happen somewhere, and this is where that gets decided rather than guessed.
/// </remarks>
internal static class FormatProbe
{
    /// <summary>What a channel order supports on this device.</summary>
    internal readonly record struct Support(bool Texture2D, bool RenderTarget, bool Display, bool SwapChain)
    {
        public bool UsableAsTarget => Texture2D && RenderTarget;
    }

    internal static Support Probe(GraphicsDevice device, Format format)
    {
        FormatSupport support = device.D3DDevice.CheckFormatSupport(format);

        return new Support(
            Texture2D: support.HasFlag(FormatSupport.Texture2D),
            RenderTarget: support.HasFlag(FormatSupport.RenderTarget),
            Display: support.HasFlag(FormatSupport.Display),
            // Flip-model swap chains take only these three formats, whatever the device reports.
            SwapChain: format is Format.R8G8B8A8_UNorm or Format.B8G8R8A8_UNorm or Format.R16G16B16A16_Float);
    }

    /// <summary>
    /// The format the shell renders in: RGBA when the device can present it, BGRA otherwise.
    /// </summary>
    internal static Format Choose(GraphicsDevice device)
    {
        Support rgba = Probe(device, Format.R8G8B8A8_UNorm);
        return rgba.UsableAsTarget && rgba.SwapChain ? Format.R8G8B8A8_UNorm : Format.B8G8R8A8_UNorm;
    }

    /// <summary>The WIC pixel format that decodes straight into <paramref name="format"/>.</summary>
    internal static Guid WicFormatFor(Format format) =>
        format == Format.R8G8B8A8_UNorm ? ImageLoader.KernelFormat : ImageLoader.Direct2DFormat;
}
