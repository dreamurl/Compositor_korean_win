using System.Reflection;
using System.Runtime.InteropServices;
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
            json = Fetch(UpdateFeed.Address);
        }
        catch (IOException exception)
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

    /// <summary>
    /// A small text file over HTTPS, redirects followed, through WinINet.
    /// </summary>
    /// <remarks>
    /// Not HttpClient: with its handlers and TLS stack compiled in, it added three megabytes to the
    /// exe to fetch a file of a hundred bytes. WinINet is in every Windows, follows GitHub's
    /// redirect to the release asset on its own, and honours the user's proxy settings.
    /// </remarks>
    private static unsafe string Fetch(string url)
    {
        nint session = InternetOpenW("Compositor_korean_win/" + Version, INTERNET_OPEN_TYPE_PRECONFIG, null, null, 0);
        if (session == 0) throw new IOException($"InternetOpen failed ({Marshal.GetLastPInvokeError()})");
        try
        {
            uint timeout = 10_000;
            InternetSetOptionW(session, INTERNET_OPTION_CONNECT_TIMEOUT, &timeout, sizeof(uint));
            InternetSetOptionW(session, INTERNET_OPTION_RECEIVE_TIMEOUT, &timeout, sizeof(uint));

            nint request = InternetOpenUrlW(session, url, null, 0,
                                            INTERNET_FLAG_RELOAD | INTERNET_FLAG_NO_CACHE_WRITE | INTERNET_FLAG_SECURE, 0);
            if (request == 0) throw new IOException($"InternetOpenUrl failed ({Marshal.GetLastPInvokeError()})");
            try
            {
                using var body = new MemoryStream();
                byte* buffer = stackalloc byte[4096];
                while (true)
                {
                    if (!InternetReadFile(request, buffer, 4096, out uint read))
                        throw new IOException($"InternetReadFile failed ({Marshal.GetLastPInvokeError()})");
                    if (read == 0) break;
                    body.Write(new ReadOnlySpan<byte>(buffer, (int)read));
                    // A feed is a hundred bytes; anything this large is not one.
                    if (body.Length > 64 * 1024) throw new IOException("the update feed is too large");
                }
                return System.Text.Encoding.UTF8.GetString(body.ToArray());
            }
            finally
            {
                InternetCloseHandle(request);
            }
        }
        finally
        {
            InternetCloseHandle(session);
        }
    }
}
