using System.Text;
using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Windows' own services for the MCP server: DirectWrite glyphs and font list, WIC decoding and
/// JPEG encoding, the subject model, and whichever image generator the machine has set up.
/// </summary>
/// <remarks>
/// Nothing here needs a graphics device or a window, so the server runs on a machine with no
/// display — a CI runner, a remote session — as well as beside the open editor.
/// </remarks>
internal sealed class ShellEditorServices : BasicEditorServices, IDisposable
{
    private readonly ImageLoader _loader = new();
    private readonly Lazy<IImageGenerator?> _generator = new(ImageGenerators.FromEnvironment);

    public override IGlyphSource? Glyphs => DirectWriteGlyphs.Shared;

    public override IReadOnlyList<string> FontFamilies() =>
        [.. DirectWriteGlyphs.Shared.Families(korean: false).Select(family => family.Name)];

    public override PixelBuffer DecodeImage(byte[] data) => _loader.Load(data, ImageLoader.KernelFormat);

    public override byte[]? EncodeJpeg(PixelBuffer pixels, double quality) =>
        ImageWriter.EncodeJpeg(pixels, quality, (1, 1, 1));

    public override ISubjectModel? SubjectModel(out string? error) => SubjectModels.Shared(out error);

    public override IImageGenerator? ImageGenerator => _generator.Value;

    public void Dispose() => _loader.Dispose();
}

/// <summary>Runs the editor as an MCP server on standard input and output (<c>--mcp</c>).</summary>
internal static class McpHost
{
    public static int Run()
    {
        // Byte streams with UTF-8 and no byte-order mark: MCP messages are UTF-8 lines, and Korean
        // layer names must survive whatever code page the console happens to have.
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), encoding);
        using var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = false, NewLine = "\n" };
        using var log = new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true };

        using var services = new ShellEditorServices();
        using var session = new EditorSession(services);
        var server = new McpServer(new McpTools(session), Updates.Version);
        log.WriteLine($"compositor MCP server {Updates.Version} ready");
        server.Run(input, output, log);
        return 0;
    }
}
