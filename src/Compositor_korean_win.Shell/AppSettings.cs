using System.Text.Json;
using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// What the program remembers between runs: for now, the interface language.
/// </summary>
/// <remarks>
/// <para>
/// Kept in <c>%APPDATA%\Compositor_korean_win\settings.json</c>, per user, and read and written
/// with the JSON reader and writer directly rather than a serializer — no reflection, which is
/// what NativeAOT asks, and nothing to generate for a file of one field.
/// </para>
/// <para>
/// A missing or unreadable file is not an error. The first run has none, and a damaged one should
/// cost the user their language choice, not the program: the choice falls back to the language
/// Windows is shown in.
/// </para>
/// </remarks>
internal static class AppSettings
{
    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Compositor_korean_win");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    /// <summary>The language saved last time, or null when none was.</summary>
    public static Language? SavedLanguage()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;

            using JsonDocument json = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("language", out JsonElement language)
                   && language.ValueKind == JsonValueKind.String
                ? Localizer.Parse(language.GetString())
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The language to start in: the saved one, or the one Windows is shown in.</summary>
    public static Language StartingLanguage() =>
        SavedLanguage() ?? Localizer.ForLanguageId(Win32.GetUserDefaultUILanguage());

    /// <summary>Writes the current choices, quietly giving up if the folder cannot be written.</summary>
    public static void Save()
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteString("language", Localizer.Code(Localizer.Current));
                writer.WriteEndObject();
            }

            Directory.CreateDirectory(Folder);

            // Written beside and moved over, so a crash mid-write leaves the old file rather than half
            // of a new one.
            string temporary = FilePath + ".tmp";
            File.WriteAllBytes(temporary, buffer.ToArray());
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("settings not saved: " + exception.Message);
        }
    }

    /// <summary>Starts in the saved language and saves every change from here on.</summary>
    public static void Apply()
    {
        Localizer.Current = StartingLanguage();
        Localizer.Changed += Save;
    }
}
