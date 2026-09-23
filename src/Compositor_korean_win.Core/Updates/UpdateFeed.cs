using System.Text.Json;

namespace Compositor_korean_win.Core;

/// <summary>What the newest release says about itself: its version and where to get it.</summary>
public sealed record UpdateInfo(Version Version, string Page);

/// <summary>
/// The update feed — this port's stand-in for upstream's Sparkle <c>appcast.xml</c>, as
/// docs/windows-port.md 8 plans it: a small JSON file attached to every GitHub release, which the
/// app reads from the latest release's download link.
/// </summary>
/// <remarks>
/// <para>
/// <c>{"version": "1.0.0", "page": "https://github.com/…/releases/tag/v1.0.0"}</c>. The page is
/// what the app opens; it does not download or install anything itself. Upstream's feed carries a
/// signature because Sparkle installs what it fetches, and this one installs nothing.
/// </para>
/// <para>
/// Read with <see cref="JsonDocument"/> rather than a serializer: two fields do not need one, and
/// a reflection-based serializer is the one part of System.Text.Json the trimmed NativeAOT build
/// would not keep.
/// </para>
/// </remarks>
public static class UpdateFeed
{
    /// <summary>Where the feed is: the latest release's copy, which GitHub redirects to.</summary>
    public const string Address = "https://github.com/dreamurl/Compositor_korean_win/releases/latest/download/update.json";

    /// <summary>The feed read, or null when it is not one.</summary>
    public static UpdateInfo? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("page", out JsonElement page) || page.ValueKind != JsonValueKind.String) return null;
            if (ParseVersion(version.GetString()) is not Version parsed) return null;

            string link = page.GetString()!;
            // Only ever a web page: this is opened in the browser, so nothing else may come through.
            return Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps
                ? new UpdateInfo(parsed, link)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// "1.2.3", "v1.2.3" or "1.2.3-beta+abc" as a version, the suffixes dropped; null when it is not
    /// one. A development build's "0.0.0-dev" is 0.0.0, older than any release.
    /// </summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string plain = text.Trim().TrimStart('v', 'V');
        int cut = plain.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) plain = plain[..cut];
        if (!Version.TryParse(plain, out Version? version)) return null;

        // Version counts a missing part as −1, so 1.2 would sort before 1.2.0.
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
    }

    /// <summary>Whether <paramref name="latest"/> is a newer release than the running <paramref name="current"/>.</summary>
    public static bool IsNewer(UpdateInfo latest, string current) =>
        ParseVersion(current) is not Version running || latest.Version > running;
}
