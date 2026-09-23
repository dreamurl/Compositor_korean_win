using System.Text;
using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>A key and the modifiers held with it.</summary>
internal readonly record struct Shortcut(int Key, bool Control = false, bool Shift = false, bool Alt = false)
{
    /// <summary>
    /// How a menu shows it. Windows spells the modifiers the same way in every language — a Korean
    /// Windows menu reads "Ctrl+O" too — so this is not translated.
    /// </summary>
    public string Display
    {
        get
        {
            var text = new StringBuilder();
            if (Control) text.Append("Ctrl+");
            if (Shift) text.Append("Shift+");
            if (Alt) text.Append("Alt+");
            text.Append(Key switch
            {
                Win32.VK_OEM_PLUS => "+",
                Win32.VK_OEM_MINUS => "-",
                Win32.VK_OEM_4 => "[",
                Win32.VK_BACK => "Backspace",
                Win32.VK_DELETE => "Del",
                Win32.VK_OEM_6 => "]",
                >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)Key).ToString(),
                _ => "?",
            });
            return text.ToString();
        }
    }
}

/// <summary>
/// One thing the user can ask for: what it is called, how to reach it, whether it can run now, and
/// what it does.
/// </summary>
/// <remarks>
/// <para>
/// The menu, the keyboard and the self-test all go through the same list, so a command cannot be
/// reachable one way and broken another. That list also makes "every command is in Korean" a thing
/// that can be checked rather than hoped: it is the list's labels.
/// </para>
/// <para>
/// <see cref="Interactive"/> marks the commands that open a window of their own — a file dialog, a
/// message box — or end the program. The self-test runs everything else.
/// </para>
/// </remarks>
internal sealed record Command(
    int Id,
    TextKey Label,
    Action Run,
    Func<bool>? Enabled = null,
    Shortcut[]? Shortcuts = null,
    bool Interactive = false)
{
    /// <summary>The label as the menu shows it now, when it depends on the document.</summary>
    public Func<string>? DynamicLabel { get; init; }

    /// <summary>Whether a tick sits beside it.</summary>
    public Func<bool>? Checked { get; init; }

    /// <summary>Whether it can run right now.</summary>
    public bool CanRun => Enabled?.Invoke() ?? true;

    public string Text => DynamicLabel?.Invoke() ?? Localizer.Text(Label);
}

/// <summary>An entry in a menu: a command, a line, or a menu of its own.</summary>
internal abstract record MenuEntry
{
    public static readonly MenuEntry Line = new Separator();

    public sealed record Item(int Command) : MenuEntry;

    public sealed record Separator : MenuEntry;

    public sealed record Submenu(TextKey Title, IReadOnlyList<MenuEntry> Entries) : MenuEntry;
}

/// <summary>Command identifiers, as the menu hands them back in WM_COMMAND.</summary>
internal static class CommandIds
{
    public const int Open = 100;
    public const int Save = 101;
    public const int SaveAs = 102;
    public const int ExportPng = 103;
    public const int Exit = 104;
    public const int ImportImages = 105;
    public const int ExportJpeg = 106;
    public const int Close = 107;
    public const int New = 108;

    public const int Undo = 200;
    public const int Redo = 201;
    public const int LanguageEnglish = 210;
    public const int LanguageKorean = 211;

    /// <summary>The Image menu's adjustments, one after another in <see cref="FilterCommand"/> order.</summary>
    public const int AdjustFirst = 300;
    public const int FlipCanvasHorizontal = 310;
    public const int FlipCanvasVertical = 311;
    public const int CanvasSize = 312;
    public const int ImageSize = 313;

    public const int SelectAll = 400;
    public const int Deselect = 401;
    public const int Inverse = 402;
    public const int SelectLayerPixels = 403;
    public const int ExpandSelection = 404;
    public const int ContractSelection = 405;
    public const int SelectMask = 406;

    public const int Cut = 220;
    public const int Copy = 221;
    public const int CopyMerged = 222;
    public const int Paste = 223;
    public const int FillForeground = 230;
    public const int FillBackground = 231;
    public const int Clear = 232;
    public const int ContentAwareFill = 233;

    public const int Invert = 306;

    public const int LayerViaCut = 531;
    public const int AddLayerMask = 540;
    public const int DeleteLayerMask = 542;
    public const int ToggleMaskLink = 543;
    public const int ToggleLayerMask = 541;

    public const int DuplicateLayer = 500;
    public const int NewLayer = 520;
    public const int DeleteLayer = 521;
    public const int MoveLayerUp = 522;
    public const int MoveLayerDown = 523;
    public const int GroupLayers = 524;
    public const int MoveOutOfGroup = 525;
    public const int ToggleVisibility = 526;
    public const int ToggleClipping = 527;
    public const int Merge = 528;
    public const int FlipLayerHorizontal = 529;
    public const int FlipLayerVertical = 530;

    /// <summary>New adjustment layers, in <see cref="FilterCommand"/> order.</summary>
    public const int AdjustmentLayerFirst = 510;

    /// <summary>The Filter menu, in <see cref="FilterCommand"/> order from Gaussian Blur on.</summary>
    public const int FilterFirst = 600;

    public const int ZoomIn = 700;
    public const int ZoomOut = 701;
    public const int FitOnScreen = 702;
    public const int ActualPixels = 703;
    public const int NextDocument = 704;
    public const int PreviousDocument = 705;

    public const int About = 900;
    public const int CheckUpdates = 901;
}
