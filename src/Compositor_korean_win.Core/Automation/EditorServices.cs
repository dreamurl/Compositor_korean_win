using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Compositor_korean_win.Core;

/// <summary>
/// What automation needs from the platform: fonts, image codecs beyond PNG, the subject model and
/// an image generator. The shell supplies Windows' own; tests supply what they need.
/// </summary>
public interface IEditorServices
{
    /// <summary>Glyph outlines for text, or null where no font engine is available.</summary>
    IGlyphSource? Glyphs { get; }

    /// <summary>Installed font families by the name text layers store.</summary>
    IReadOnlyList<string> FontFamilies();

    /// <summary>An encoded image (PNG, JPEG, …) as premultiplied pixels; throws when it will not decode.</summary>
    PixelBuffer DecodeImage(byte[] data);

    /// <summary>A JPEG of <paramref name="pixels"/> over white, or null where no encoder is available.</summary>
    byte[]? EncodeJpeg(PixelBuffer pixels, double quality);

    /// <summary>The background-removal model, or null with the reason.</summary>
    ISubjectModel? SubjectModel(out string? error);

    /// <summary>Makes images from words, or null when none is set up.</summary>
    IImageGenerator? ImageGenerator { get; }
}

/// <summary>Only what Core has itself: PNG in and out, and whatever glyphs it is given.</summary>
public class BasicEditorServices(IGlyphSource? glyphs = null) : IEditorServices
{
    public virtual IGlyphSource? Glyphs => glyphs;

    public virtual IReadOnlyList<string> FontFamilies() => [];

    public virtual PixelBuffer DecodeImage(byte[] data) => Png.Decode(data);

    public virtual byte[]? EncodeJpeg(PixelBuffer pixels, double quality) => null;

    public virtual ISubjectModel? SubjectModel(out string? error)
    {
        error = "background removal needs the build that includes the AI model";
        return null;
    }

    public virtual IImageGenerator? ImageGenerator => null;
}

/// <summary>Something that turns a prompt into an encoded image.</summary>
public interface IImageGenerator
{
    /// <summary>How it makes images, for the answer: "codex", "openai (gpt-image-2)", the command.</summary>
    string Name { get; }

    /// <summary>The image's encoded bytes; throws <see cref="ToolException"/> when nothing came back.</summary>
    byte[] Generate(string prompt, int width, int height);
}

/// <summary>
/// Chooses an image generator from the environment.
/// </summary>
/// <remarks>
/// <para>
/// Claude cannot draw a photograph; Codex can, through its built-in <c>$imagegen</c>, and so can
/// OpenAI's image API directly. Whichever is available is used, in this order:
/// </para>
/// <list type="number">
/// <item><c>COMPOSITOR_IMAGE_COMMAND</c> — any command, with <c>{prompt}</c>, <c>{output}</c>,
/// <c>{width}</c> and <c>{height}</c> filled in, that leaves a PNG at <c>{output}</c>.</item>
/// <item><c>OPENAI_API_KEY</c> — OpenAI's images endpoint, model <c>COMPOSITOR_IMAGE_MODEL</c>
/// (default <c>gpt-image-2</c>).</item>
/// <item>The <c>codex</c> command on the PATH, signed in — <c>codex exec</c> asked to use
/// <c>$imagegen</c> and save the picture where it is told.</item>
/// </list>
/// <para><c>COMPOSITOR_IMAGE_GENERATOR</c> (<c>command</c>, <c>openai</c>, <c>codex</c>) forces one.</para>
/// </remarks>
public static class ImageGenerators
{
    public const string CodexTemplate =
        "codex exec --skip-git-repo-check --full-auto \"Use $imagegen to create this image: {prompt} " +
        "The image should be about {width} by {height} pixels. Save it as a PNG file at exactly this path: " +
        "{output} and do nothing else.\"";

    public static IImageGenerator? FromEnvironment()
    {
        string? forced = Environment.GetEnvironmentVariable("COMPOSITOR_IMAGE_GENERATOR")?.Trim().ToLowerInvariant();
        string? command = Environment.GetEnvironmentVariable("COMPOSITOR_IMAGE_COMMAND");
        string? key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string model = Environment.GetEnvironmentVariable("COMPOSITOR_IMAGE_MODEL") is { Length: > 0 } named ? named : "gpt-image-2";

        return forced switch
        {
            "command" when !string.IsNullOrWhiteSpace(command) => new CommandImageGenerator(command, "command"),
            "openai" when !string.IsNullOrWhiteSpace(key) => new OpenAiImageGenerator(key, model),
            "codex" => new CommandImageGenerator(CodexTemplate, "codex"),
            _ when !string.IsNullOrWhiteSpace(command) => new CommandImageGenerator(command, "command"),
            _ when !string.IsNullOrWhiteSpace(key) => new OpenAiImageGenerator(key, model),
            _ when OnPath("codex") => new CommandImageGenerator(CodexTemplate, "codex"),
            _ => null,
        };
    }

    private static bool OnPath(string program)
    {
        string[] names = OperatingSystem.IsWindows() ? [program + ".exe", program + ".cmd", program + ".bat"] : [program];
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (folder.Length == 0) continue;
            foreach (string name in names)
                if (File.Exists(Path.Combine(folder.Trim('"'), name))) return true;
        }
        return false;
    }
}

/// <summary>Runs a command that writes a PNG, waiting up to five minutes.</summary>
public sealed class CommandImageGenerator(string template, string name) : IImageGenerator
{
    public string Name => name;

    /// <summary>The command line for one request — separate so it can be checked without running anything.</summary>
    public static string Fill(string template, string prompt, string output, int width, int height)
    {
        // Double quotes would end the prompt's quoting on any shell; single quotes read the same.
        string safe = prompt.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');
        return template.Replace("{prompt}", safe).Replace("{output}", output)
                       .Replace("{width}", width.ToString(System.Globalization.CultureInfo.InvariantCulture))
                       .Replace("{height}", height.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public byte[] Generate(string prompt, int width, int height)
    {
        string folder = Path.Combine(Path.GetTempPath(), "compositor-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, "image.png");
        string line = Fill(template, prompt, output, width, height);

        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + line + "\"")
            : new ProcessStartInfo("/bin/sh", ["-c", line]);
        start.WorkingDirectory = folder;
        start.UseShellExecute = false;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.CreateNoWindow = true;

        try
        {
            using Process process = Process.Start(start) ?? throw new ToolException($"could not start: {line}");
            process.StandardInput.Close();
            Task<string> errors = process.StandardError.ReadToEndAsync();
            Task<string> said = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new ToolException("the image generator did not finish within five minutes");
            }

            if (File.Exists(output)) return File.ReadAllBytes(output);

            // A generator told to save one file sometimes saves another; take the newest image.
            string? other = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp")
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (other is not null) return File.ReadAllBytes(other);

            string tail = (errors.Result + "\n" + said.Result).Trim();
            if (tail.Length > 600) tail = tail[^600..];
            throw new ToolException($"{name} exited with {process.ExitCode} and left no image. {tail}");
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>OpenAI's images endpoint, asked for a base64 PNG.</summary>
public sealed class OpenAiImageGenerator(string key, string model) : IImageGenerator
{
    public string Name => $"openai ({model})";

    /// <summary>The nearest size the endpoint offers: square, landscape or portrait.</summary>
    public static string SizeFor(int width, int height)
    {
        double ratio = (double)width / Math.Max(1, height);
        return ratio > 1.2 ? "1536x1024" : ratio < 1 / 1.2 ? "1024x1536" : "1024x1024";
    }

    public byte[] Generate(string prompt, int width, int height)
    {
        using var body = new MemoryStream();
        using (var writer = new Utf8JsonWriter(body))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteString("prompt", prompt);
            writer.WriteString("size", SizeFor(width, height));
            writer.WriteNumber("n", 1);
            writer.WriteEndObject();
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/images/generations")
        {
            Content = new ByteArrayContent(body.ToArray()),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using HttpResponseMessage response = http.Send(request);
        using var reader = new StreamReader(response.Content.ReadAsStream(), Encoding.UTF8);
        string text = reader.ReadToEnd();
        if (!response.IsSuccessStatusCode)
            throw new ToolException($"OpenAI answered {(int)response.StatusCode}: {(text.Length > 400 ? text[..400] : text)}");

        using JsonDocument answer = JsonDocument.Parse(text);
        if (answer.RootElement.TryGetProperty("data", out JsonElement data) && data.GetArrayLength() > 0
            && data[0].TryGetProperty("b64_json", out JsonElement encoded) && encoded.GetString() is string base64)
            return Convert.FromBase64String(base64);
        throw new ToolException("OpenAI returned no image.");
    }
}
