using System.Reflection;
using Compositor_korean_win.Core;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

/// <summary>Which version this is, and Help › Check for Updates.</summary>
/// <remarks>
/// <para>
/// The version is stamped by CI from the release tag (<c>-p:Version=1.0.0</c>); a build made any
/// other way says 0.0.0-dev, which any release is newer than.
/// </para>
/// <para>
/// The check only ever runs when asked — nothing is sent anywhere by starting the app — and it only
/// opens the release page in the browser. Choosing and installing the build is left to the user,
/// the way docs/windows-port.md 8 plans the minimum: no silent downloads, so no signature to check.
/// </para>
/// </remarks>
internal static class Updates
{
    /// <summary>The running version, as the release tag gave it.</summary>
    public static string Version { get; } =
        typeof(Updates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion is string stamped
            ? stamped.Split('+')[0]
            : "0.0.0-dev";

    /// <summary>Fetches the feed, compares, and says what it found — offering the page when there is something newer.</summary>
    public static void Check(nint owner)
    {
        string title = Localizer.Text(TextKey.AppTitle);
        string? json;
        string? failure = null;

        try
        {
            // A short wait on a manual check: the user is looking at the menu, not at a spinner.
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Compositor_korean_win/" + Version);
            json = client.GetStringAsync(UpdateFeed.Address).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            json = null;
            failure = exception.Message;
        }

        if (json is null || UpdateFeed.Parse(json) is not UpdateInfo latest)
        {
            MessageBoxW(owner, Localizer.Format(TextKey.UpdateFailed, failure ?? Localizer.Text(TextKey.UpdateUnreadable)),
                        title, MB_OK | MB_ICONWARNING);
            return;
        }

        if (!UpdateFeed.IsNewer(latest, Version))
        {
            MessageBoxW(owner, Localizer.Format(TextKey.UpdateLatest, Version), title, MB_OK | MB_ICONINFORMATION);
            return;
        }

        string latestText = $"{latest.Version.Major}.{latest.Version.Minor}.{latest.Version.Build}";
        if (MessageBoxW(owner, Localizer.Format(TextKey.UpdateAvailable, latestText, Version), title,
                        MB_YESNO | MB_ICONINFORMATION) == IDYES)
        {
            ShellExecuteW(owner, "open", latest.Page, null, null, SW_SHOWNORMAL);
        }
    }
}
