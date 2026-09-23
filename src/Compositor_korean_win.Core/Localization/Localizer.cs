using System.Globalization;

namespace Compositor_korean_win.Core;

/// <summary>The languages the interface can be shown in.</summary>
public enum Language
{
    English,
    Korean,
}

/// <summary>
/// The interface's words, in whichever language is chosen.
/// </summary>
/// <remarks>
/// <para>
/// Every word the user can read goes through here, keyed by <see cref="TextKey"/>. The words
/// themselves live in one table (<c>TextTable.cs</c>) with each language side by side, so a missing
/// translation is a gap in a row rather than a file somebody forgot to update — and the tests read
/// that table and fail on it.
/// </para>
/// <para>
/// docs/windows-port.md §6 planned <c>.resx</c>. Its per-language resources ship as satellite
/// assemblies, one DLL per language beside the executable, which is exactly what the single-exe
/// build exists to avoid; two languages do not need the machinery.
/// </para>
/// <para>
/// The language can change while the program runs. <see cref="Changed"/> tells the shell to
/// rebuild what it shows; anything that stores a name for later — the history, most importantly —
/// stores the key and asks for the words when they are shown.
/// </para>
/// </remarks>
public static partial class Localizer
{
    private static Language s_current = Language.English;

    /// <summary>The language the interface is in.</summary>
    public static Language Current
    {
        get => s_current;
        set
        {
            if (s_current == value) return;
            s_current = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>The words for <paramref name="key"/> in the current language.</summary>
    public static string Text(TextKey key) => TextTable.Lookup(key, Current);

    /// <summary>The words for <paramref name="key"/> in <paramref name="language"/>.</summary>
    public static string Text(TextKey key, Language language) => TextTable.Lookup(key, language);

    /// <summary>The words for <paramref name="key"/> with its placeholders filled in.</summary>
    public static string Format(TextKey key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Text(key), arguments);

    /// <summary>
    /// A menu label as ordinary words: without the letter Alt reaches it by ("&amp;File",
    /// "파일(&amp;F)") or the ellipsis that says a window opens.
    /// </summary>
    /// <remarks>What the history and the undo item show when they reuse a command's name.</remarks>
    public static string Plain(string label)
    {
        string text = MnemonicInBrackets().Replace(label, string.Empty);
        return text.Replace("&", string.Empty).TrimEnd('…').Trim();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\(&.\)")]
    private static partial System.Text.RegularExpressions.Regex MnemonicInBrackets();

    /// <summary>A language's name in that language, as a language picker shows it.</summary>
    /// <remarks>The same in every language, so the way back is readable whatever is chosen.</remarks>
    public static string NativeName(Language language) =>
        Text(language == Language.Korean ? TextKey.LanguageKorean : TextKey.LanguageEnglish, language);

    /// <summary>The code a settings file stores a language as.</summary>
    public static string Code(Language language) => language == Language.Korean ? "ko" : "en";

    /// <summary>A stored code read back, or null when it names no language this knows.</summary>
    public static Language? Parse(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "ko" or "ko-kr" => Language.Korean,
        "en" or "en-us" or "en-gb" => Language.English,
        _ => null,
    };

    /// <summary>
    /// The language for a Windows language identifier: Korean for any Korean locale, English
    /// otherwise.
    /// </summary>
    /// <remarks>The low ten bits of a LANGID are the primary language; Korean is 0x12.</remarks>
    public static Language ForLanguageId(int languageId) =>
        (languageId & 0x3FF) == 0x12 ? Language.Korean : Language.English;
}
