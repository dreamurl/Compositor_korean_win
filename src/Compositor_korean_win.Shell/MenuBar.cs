using Compositor_korean_win.Core;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The window's menu bar, built from the command list and rebuilt whenever the language changes.
/// </summary>
/// <remarks>
/// <para>
/// A native Win32 menu rather than one drawn with Direct2D. docs/windows-port.md §5.1 chose
/// self-drawn widgets for the panels, where the canvas's look matters; a menu bar is the one piece
/// of chrome every Windows program shares, and the system's own is keyboard-navigable, reads
/// Korean with the system font, follows the user's accessibility settings and costs nothing in the
/// binary.
/// </para>
/// <para>
/// Nothing about a command's state is stored in the menu. Enabled, ticked and — for Undo and Redo —
/// the label itself are asked of the command as each menu opens, so the menu can never show a state
/// the program is no longer in.
/// </para>
/// </remarks>
internal sealed unsafe class MenuBar : IDisposable
{
    private readonly nint _window;
    private readonly Dictionary<int, Command> _commands;
    private readonly IReadOnlyList<MenuEntry.Submenu> _layout;
    private nint _bar;

    public MenuBar(nint window, IEnumerable<Command> commands, IReadOnlyList<MenuEntry.Submenu> layout)
    {
        _window = window;
        _commands = commands.ToDictionary(command => command.Id);
        _layout = layout;

        Build();
        Localizer.Changed += Build;
    }

    public IReadOnlyCollection<Command> Commands => _commands.Values;

    /// <summary>The menu bar itself, for walking in the self-test.</summary>
    public nint Handle => _bar;

    /// <summary>Makes the whole bar again, in the current language.</summary>
    private void Build()
    {
        nint bar = CreateMenu();
        foreach (MenuEntry.Submenu menu in _layout)
            AppendMenuW(bar, MF_POPUP, (nuint)Populate(menu.Entries), Localizer.Text(menu.Title));

        SetMenu(_window, bar);
        if (_bar != 0) DestroyMenu(_bar);
        _bar = bar;
        DrawMenuBar(_window);
    }

    private nint Populate(IReadOnlyList<MenuEntry> entries)
    {
        nint popup = CreatePopupMenu();

        foreach (MenuEntry entry in entries)
        {
            switch (entry)
            {
                case MenuEntry.Item item when _commands.TryGetValue(item.Command, out Command? command):
                    AppendMenuW(popup, MF_STRING, (nuint)command.Id, Label(command));
                    break;

                case MenuEntry.Separator:
                    AppendMenuW(popup, MF_SEPARATOR, 0, null);
                    break;

                case MenuEntry.Submenu submenu:
                    AppendMenuW(popup, MF_POPUP, (nuint)Populate(submenu.Entries), Localizer.Text(submenu.Title));
                    break;
            }
        }

        return popup;
    }

    /// <summary>The label with its shortcut after a tab, which Windows right-aligns.</summary>
    private static string Label(Command command) =>
        command.Shortcuts is [Shortcut first, ..] ? command.Text + "\t" + first.Display : command.Text;

    /// <summary>
    /// True while nothing on the menus may run — a sheet is open, and holds the rest of the window
    /// still until it closes. The items grey out rather than vanish, as they do under any dialog.
    /// </summary>
    public Func<bool>? Blocked { get; set; }

    /// <summary>Brings a menu that is about to open up to date. Answers WM_INITMENUPOPUP.</summary>
    public void Refresh(nint popup)
    {
        int count = GetMenuItemCount(popup);
        for (int position = 0; position < count; position++)
        {
            uint id = GetMenuItemID(popup, position);
            if (id is 0 or uint.MaxValue || !_commands.TryGetValue((int)id, out Command? command)) continue;

            // Relabelling resets the item's state, so it goes first.
            if (command.DynamicLabel is not null)
                ModifyMenuW(popup, (uint)position, MF_BYPOSITION | MF_STRING, id, Label(command));

            EnableMenuItem(popup, id, MF_BYCOMMAND | (command.CanRun && Blocked?.Invoke() != true ? MF_ENABLED : MF_GRAYED));

            if (command.Checked is Func<bool> ticked)
                CheckMenuItem(popup, id, MF_BYCOMMAND | (ticked() ? MF_CHECKED : MF_UNCHECKED));
        }
    }

    /// <summary>
    /// A right-click menu of commands at a screen point: the same labels, shortcuts and greyed-out
    /// states as the menu bar, since it is built and refreshed the same way. True when one ran.
    /// </summary>
    public bool Popup(IReadOnlyList<MenuEntry> entries, int x, int y)
    {
        if (Blocked?.Invoke() == true) return false;

        nint popup = Populate(entries);
        try
        {
            Refresh(popup);
            int chosen = TrackPopupMenuEx(popup, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON, x, y, _window, 0);
            return chosen != 0 && Run(chosen);
        }
        finally
        {
            DestroyMenu(popup);
        }
    }

    /// <summary>Runs a command the menu chose. Answers WM_COMMAND.</summary>
    public bool Run(int id)
    {
        if (Blocked?.Invoke() == true) return false;
        if (!_commands.TryGetValue(id, out Command? command) || !command.CanRun) return false;
        command.Run();
        return true;
    }

    /// <summary>
    /// Runs the command a key press belongs to. True when the key is a command's, whether or not it
    /// could run — a disabled command's key must not fall through to the canvas as something else.
    /// </summary>
    public bool TryShortcut(Shortcut pressed)
    {
        // One key can belong to several commands - Delete clears a selection or, with none, deletes
        // the layer - so the first that can run takes it.
        bool claimed = false;
        foreach (Command command in _commands.Values.OrderBy(command => command.Id))
        {
            if (command.Shortcuts is null || !command.Shortcuts.Contains(pressed)) continue;
            claimed = true;
            if (!command.CanRun) continue;
            command.Run();
            return true;
        }
        return claimed;
    }

    /// <summary>Every label in the bar, submenus included, as the user would read it.</summary>
    public List<string> Labels()
    {
        var labels = new List<string>();
        Walk(_bar, labels);
        return labels;

        static void Walk(nint menu, List<string> into)
        {
            int count = GetMenuItemCount(menu);
            char* text = stackalloc char[512];

            for (int position = 0; position < count; position++)
            {
                int length = GetMenuStringW(menu, (uint)position, text, 512, MF_BYPOSITION);
                if (length > 0) into.Add(new string(text, 0, length));

                nint submenu = GetSubMenu(menu, position);
                if (submenu != 0) Walk(submenu, into);
            }
        }
    }

    public void Dispose()
    {
        Localizer.Changed -= Build;
        if (_bar == 0) return;

        SetMenu(_window, 0);
        DestroyMenu(_bar);
        _bar = 0;
    }
}
