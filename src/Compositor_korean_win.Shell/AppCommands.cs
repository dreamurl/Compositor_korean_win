using Compositor_korean_win.Core;
using static Compositor_korean_win.Shell.Win32;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Every command the program has, and the menus they sit in.
/// </summary>
/// <remarks>
/// <para>
/// The menus follow upstream's — File, Edit, Image, Layer, Select, Filter, View — with Photoshop for
/// Windows' shortcuts where upstream's Mac ones translate (⌘ to Ctrl). Two things move to where a
/// Windows user looks for them: the language, under Edit › Preferences as in Photoshop, and About,
/// under Help rather than the application menu Windows does not have.
/// </para>
/// <para>
/// Only commands that work are listed. The rest of upstream's menus arrive with the Core operations
/// behind them (docs/progress.md 7, M6.3).
/// </para>
/// </remarks>
internal static class AppCommands
{
    private static readonly FilterCommand[] Adjustments =
    [
        FilterCommand.Levels, FilterCommand.Curves, FilterCommand.HueSaturation,
        FilterCommand.Exposure, FilterCommand.GradientMap, FilterCommand.Grain,
    ];

    private static readonly FilterCommand[] Filters =
    [
        FilterCommand.GaussianBlur, FilterCommand.MotionBlur, FilterCommand.AddNoise, FilterCommand.LensCorrection,
    ];

    public static (List<Command> Commands, List<MenuEntry.Submenu> Layout) Create(
        CanvasView canvas, DocumentFiles files, nint owner)
    {
        bool Idle() => !canvas.IsFiltering;
        bool Editable() => canvas.CanEdit;
        var clipboard = new Clipboard(owner);

        var commands = new List<Command>
        {
            new(CommandIds.Open, TextKey.CommandOpen, files.Open, Idle, [new(VK_O, Control: true)], Interactive: true),
            new(CommandIds.Save, TextKey.CommandSave, files.Save, Editable, [new(VK_S, Control: true)], Interactive: true),
            new(CommandIds.SaveAs, TextKey.CommandSaveAs, files.SaveAs, Editable,
                [new(VK_S, Control: true, Shift: true)], Interactive: true),
            new(CommandIds.ExportPng, TextKey.CommandExportPng, files.ExportPng, Editable,
                [new(VK_E, Control: true, Shift: true)], Interactive: true),
            new(CommandIds.Exit, TextKey.CommandExit, () => PostQuitMessage(0), null,
                [new(VK_Q, Control: true)], Interactive: true),

            new(CommandIds.Undo, TextKey.CommandUndo, canvas.Undo, () => canvas.CanUndo, [new(VK_Z, Control: true)])
            {
                DynamicLabel = () => canvas.CanUndo
                    ? Localizer.Format(TextKey.CommandUndoNamed, canvas.UndoName)
                    : Localizer.Text(TextKey.CommandUndo),
            },
            new(CommandIds.Redo, TextKey.CommandRedo, canvas.Redo, () => canvas.CanRedo,
                [new(VK_Z, Control: true, Shift: true), new(VK_Y, Control: true)])
            {
                DynamicLabel = () => canvas.CanRedo
                    ? Localizer.Format(TextKey.CommandRedoNamed, canvas.RedoName)
                    : Localizer.Text(TextKey.CommandRedo),
            },

            // Changing the language is not something the self-test's command run should do behind
            // its back — it checks each language on purpose — so these count as interactive.
            new(CommandIds.LanguageEnglish, TextKey.LanguageEnglish, () => Localizer.Current = Language.English,
                Interactive: true)
            {
                Checked = () => Localizer.Current == Language.English,
            },
            new(CommandIds.LanguageKorean, TextKey.LanguageKorean, () => Localizer.Current = Language.Korean,
                Interactive: true)
            {
                Checked = () => Localizer.Current == Language.Korean,
            },

            new(CommandIds.SelectAll, TextKey.CommandSelectAll, canvas.SelectAll, Editable, [new(VK_A, Control: true)]),
            new(CommandIds.Deselect, TextKey.CommandDeselect, canvas.Deselect,
                () => Editable() && canvas.HasSelection, [new(VK_D, Control: true)]),

            new(CommandIds.DuplicateLayer, TextKey.CommandDuplicateLayer, canvas.LayerViaCopy,
                () => canvas.CanDuplicateLayer, [new(VK_J, Control: true)])
            {
                DynamicLabel = () => Localizer.Text(canvas.HasSelection ? TextKey.CommandLayerViaCopy : TextKey.CommandDuplicateLayer),
            },
            new(CommandIds.LayerViaCut, TextKey.CommandLayerViaCut, canvas.LayerViaCut, () => canvas.CanEditSelectedPixels,
                [new(VK_J, Control: true, Shift: true)]),

            new(CommandIds.Cut, TextKey.CommandCut, () =>
                {
                    if (canvas.CopyPixels(merged: false) is not var (pixels, placement)) return;
                    clipboard.Put(pixels, placement);
                    pixels.Release();
                    canvas.ClearAfterCut();
                }, () => canvas.CanEditSelectedPixels, [new(VK_X, Control: true)]),
            new(CommandIds.Copy, TextKey.CommandCopy, () =>
                {
                    if (canvas.CopyPixels(merged: false) is not var (pixels, placement)) return;
                    clipboard.Put(pixels, placement);
                    pixels.Release();
                }, () => canvas.CanEditExistingPixels, [new(VK_C, Control: true)]),
            new(CommandIds.CopyMerged, TextKey.CommandCopyMerged, () =>
                {
                    if (canvas.CopyPixels(merged: true) is not var (pixels, placement)) return;
                    clipboard.Put(pixels, placement);
                    pixels.Release();
                }, Editable, [new(VK_C, Control: true, Shift: true)]),
            new(CommandIds.Paste, TextKey.CommandPaste, () =>
                {
                    if (clipboard.Take() is var (pixels, placement)) canvas.Paste(pixels, placement);
                }, Editable, [new(VK_V, Control: true)]),

            new(CommandIds.FillForeground, TextKey.CommandFillForeground, () => canvas.Fill(foreground: true),
                () => canvas.CanEditPixels, [new(VK_BACK, Alt: true)]),
            new(CommandIds.FillBackground, TextKey.CommandFillBackground, () => canvas.Fill(foreground: false),
                () => canvas.CanEditPixels, [new(VK_BACK, Control: true)]),
            new(CommandIds.Clear, TextKey.CommandClear, canvas.Clear, () => canvas.CanEditSelectedPixels, [new(VK_DELETE)]),
            new(CommandIds.ContentAwareFill, TextKey.CommandContentAwareFill, canvas.ContentAwareFill,
                () => canvas.CanEditSelectedPixels, [new(VK_BACK, Shift: true)]),

            new(CommandIds.Inverse, TextKey.CommandInverse, canvas.Inverse, () => canvas.CanInverse,
                [new(VK_I, Control: true, Shift: true)]),
            new(CommandIds.SelectLayerPixels, TextKey.CommandSelectLayerPixels, canvas.SelectLayerPixels,
                () => canvas.CanSelectLayerPixels),
            new(CommandIds.ExpandSelection, TextKey.CommandExpandSelection, () => canvas.GrowSelection(outwards: true),
                () => canvas.CanInverse)
            {
                DynamicLabel = () => Localizer.Format(TextKey.CommandExpandSelection, canvas.SelectionStep),
            },
            new(CommandIds.ContractSelection, TextKey.CommandContractSelection, () => canvas.GrowSelection(outwards: false),
                () => canvas.CanInverse)
            {
                DynamicLabel = () => Localizer.Format(TextKey.CommandContractSelection, canvas.SelectionStep),
            },

            new(CommandIds.Invert, TextKey.CommandInvert, canvas.Invert, () => canvas.CanEditExistingPixels,
                [new(VK_I, Control: true)]),

            new(CommandIds.AddLayerMask, TextKey.CommandAddLayerMask, canvas.AddMask, () => canvas.CanAddMask),
            new(CommandIds.DeleteLayerMask, TextKey.CommandDeleteLayerMask, canvas.DeleteMask, () => canvas.CanChangeMask),
            new(CommandIds.ToggleLayerMask, TextKey.CommandDisableLayerMask, canvas.ToggleMask, () => canvas.CanChangeMask)
            {
                DynamicLabel = () => Localizer.Text(canvas.MaskEnabled ? TextKey.CommandDisableLayerMask : TextKey.CommandEnableLayerMask),
            },
            new(CommandIds.NewLayer, TextKey.CommandNewLayer, canvas.AddLayer, Editable,
                [new(VK_N, Control: true, Shift: true)]),
            new(CommandIds.DeleteLayer, TextKey.CommandDeleteLayer, canvas.DeleteLayers, () => canvas.CanDeleteLayers,
                [new(VK_DELETE)]),
            new(CommandIds.MoveLayerUp, TextKey.CommandMoveLayerUp, () => canvas.MoveLayer(1), () => canvas.CanMoveLayer(1),
                [new(VK_OEM_6, Control: true)]),
            new(CommandIds.MoveLayerDown, TextKey.CommandMoveLayerDown, () => canvas.MoveLayer(-1),
                () => canvas.CanMoveLayer(-1), [new(VK_OEM_4, Control: true)]),
            new(CommandIds.GroupLayers, TextKey.CommandGroupLayers, canvas.GroupLayers, () => canvas.CanGroupLayers,
                [new(VK_G, Control: true)]),
            new(CommandIds.MoveOutOfGroup, TextKey.CommandMoveOutOfGroup, canvas.MoveOutOfGroup,
                () => canvas.CanMoveOutOfGroup),
            new(CommandIds.ToggleVisibility, TextKey.CommandHideLayer, canvas.ToggleVisibility,
                () => canvas.CanToggleVisibility)
            {
                DynamicLabel = () => Localizer.Text(canvas.ActiveLayerVisible ? TextKey.CommandHideLayer : TextKey.CommandShowLayer),
            },
            new(CommandIds.ToggleClipping, TextKey.CommandCreateClippingMask, canvas.ToggleClipping,
                () => canvas.CanToggleClipping, [new(VK_G, Control: true, Alt: true)])
            {
                DynamicLabel = () => Localizer.Text(canvas.ActiveLayerClipped
                    ? TextKey.CommandReleaseClippingMask : TextKey.CommandCreateClippingMask),
            },
            new(CommandIds.Merge, TextKey.CommandMergeDown, canvas.Merge, () => canvas.MergePlan is not null,
                [new(VK_E, Control: true)])
            {
                DynamicLabel = () => Localizer.Text(canvas.MergePlan?.Action ?? TextKey.CommandMergeDown),
            },
            new(CommandIds.FlipLayerHorizontal, TextKey.CommandFlipLayerHorizontal, () => canvas.FlipLayers(horizontally: true),
                () => canvas.CanFlipLayers),
            new(CommandIds.FlipLayerVertical, TextKey.CommandFlipLayerVertical, () => canvas.FlipLayers(horizontally: false),
                () => canvas.CanFlipLayers),
            new(CommandIds.FlipCanvasHorizontal, TextKey.CommandFlipCanvasHorizontal, () => canvas.FlipCanvas(horizontally: true),
                Editable),
            new(CommandIds.FlipCanvasVertical, TextKey.CommandFlipCanvasVertical, () => canvas.FlipCanvas(horizontally: false),
                Editable),

            new(CommandIds.ZoomIn, TextKey.CommandZoomIn, () => canvas.Zoom(closer: true), () => canvas.HasDocument,
                [new(VK_OEM_PLUS, Control: true), new(VK_ADD, Control: true)]),
            new(CommandIds.ZoomOut, TextKey.CommandZoomOut, () => canvas.Zoom(closer: false), () => canvas.HasDocument,
                [new(VK_OEM_MINUS, Control: true), new(VK_SUBTRACT, Control: true)]),
            new(CommandIds.FitOnScreen, TextKey.CommandFitOnScreen, canvas.FitOnScreen, () => canvas.HasDocument,
                [new(VK_0, Control: true)]),
            new(CommandIds.ActualPixels, TextKey.CommandActualPixels, canvas.ActualPixels, () => canvas.HasDocument,
                [new(VK_1, Control: true)]),

            new(CommandIds.About, TextKey.CommandAbout,
                () => MessageBoxW(owner, Localizer.Text(TextKey.AboutText), Localizer.Text(TextKey.AppTitle),
                                  MB_OK | MB_ICONINFORMATION),
                Interactive: true),
        };

        // Image › Adjustments: run over the chosen layer's pixels. Photoshop's keys for the three
        // that have them.
        foreach (FilterCommand adjustment in Adjustments)
        {
            Shortcut[]? keys = adjustment switch
            {
                FilterCommand.Levels => [new(VK_L, Control: true)],
                FilterCommand.Curves => [new(VK_M, Control: true)],
                FilterCommand.HueSaturation => [new(VK_U, Control: true)],
                _ => null,
            };

            commands.Add(new Command(CommandIds.AdjustFirst + (int)adjustment, CanvasView.FilterTitle(adjustment),
                                     () => canvas.StartFilter(adjustment, asLayer: false), () => canvas.CanFilter, keys)
            {
                DynamicLabel = Ellipsis(CanvasView.FilterTitle(adjustment)),
            });

            commands.Add(new Command(CommandIds.AdjustmentLayerFirst + (int)adjustment, CanvasView.FilterTitle(adjustment),
                                     () => canvas.StartFilter(adjustment, asLayer: true),
                                     () => canvas.CanAddAdjustmentLayer)
            {
                DynamicLabel = Ellipsis(CanvasView.FilterTitle(adjustment)),
            });
        }

        foreach (FilterCommand filter in Filters)
        {
            commands.Add(new Command(CommandIds.FilterFirst + (int)filter, CanvasView.FilterTitle(filter),
                                     () => canvas.StartFilter(filter, asLayer: false), () => canvas.CanFilter)
            {
                DynamicLabel = Ellipsis(CanvasView.FilterTitle(filter)),
            });
        }

        var layout = new List<MenuEntry.Submenu>
        {
            new(TextKey.MenuFile,
            [
                Item(CommandIds.Open), MenuEntry.Line,
                Item(CommandIds.Save), Item(CommandIds.SaveAs), Item(CommandIds.ExportPng), MenuEntry.Line,
                Item(CommandIds.Exit),
            ]),
            new(TextKey.MenuEdit,
            [
                Item(CommandIds.Undo), Item(CommandIds.Redo), MenuEntry.Line,
                Item(CommandIds.Cut), Item(CommandIds.Copy), Item(CommandIds.CopyMerged), Item(CommandIds.Paste),
                MenuEntry.Line,
                Item(CommandIds.FillForeground), Item(CommandIds.FillBackground), Item(CommandIds.Clear),
                Item(CommandIds.ContentAwareFill), MenuEntry.Line,
                new MenuEntry.Submenu(TextKey.MenuPreferences,
                [
                    new MenuEntry.Submenu(TextKey.MenuLanguage,
                        [Item(CommandIds.LanguageEnglish), Item(CommandIds.LanguageKorean)]),
                ]),
            ]),
            new(TextKey.MenuImage,
            [
                new MenuEntry.Submenu(TextKey.MenuAdjustments,
                [
                    .. Adjustments.Select(adjustment => Item(CommandIds.AdjustFirst + (int)adjustment)),
                    MenuEntry.Line,
                    Item(CommandIds.Invert),
                ]),
                MenuEntry.Line,
                Item(CommandIds.FlipCanvasHorizontal), Item(CommandIds.FlipCanvasVertical),
            ]),
            new(TextKey.MenuLayer,
            [
                Item(CommandIds.NewLayer), Item(CommandIds.DuplicateLayer), Item(CommandIds.LayerViaCut),
                Item(CommandIds.DeleteLayer), MenuEntry.Line,
                new MenuEntry.Submenu(TextKey.MenuLayerMask,
                    [Item(CommandIds.AddLayerMask), Item(CommandIds.DeleteLayerMask), Item(CommandIds.ToggleLayerMask)]),
                new MenuEntry.Submenu(TextKey.MenuNewAdjustmentLayer,
                    [.. Adjustments.Select(adjustment => Item(CommandIds.AdjustmentLayerFirst + (int)adjustment))]),
                MenuEntry.Line,
                Item(CommandIds.ToggleClipping), MenuEntry.Line,
                Item(CommandIds.GroupLayers), Item(CommandIds.MoveOutOfGroup), MenuEntry.Line,
                Item(CommandIds.ToggleVisibility), MenuEntry.Line,
                Item(CommandIds.MoveLayerUp), Item(CommandIds.MoveLayerDown), MenuEntry.Line,
                Item(CommandIds.Merge), MenuEntry.Line,
                Item(CommandIds.FlipLayerHorizontal), Item(CommandIds.FlipLayerVertical),
            ]),
            new(TextKey.MenuSelect,
            [
                Item(CommandIds.SelectAll), Item(CommandIds.Deselect), Item(CommandIds.Inverse), MenuEntry.Line,
                Item(CommandIds.SelectLayerPixels), MenuEntry.Line,
                Item(CommandIds.ExpandSelection), Item(CommandIds.ContractSelection),
            ]),
            new(TextKey.MenuFilter, [.. Filters.Select(filter => Item(CommandIds.FilterFirst + (int)filter))]),
            new(TextKey.MenuView,
            [
                Item(CommandIds.ZoomIn), Item(CommandIds.ZoomOut), MenuEntry.Line,
                Item(CommandIds.FitOnScreen), Item(CommandIds.ActualPixels),
            ]),
            new(TextKey.MenuHelp, [Item(CommandIds.About)]),
        };

        return (commands, layout);

        static MenuEntry Item(int id) => new MenuEntry.Item(id);

        // An entry that opens a window of its own ends in an ellipsis, in either language.
        static Func<string> Ellipsis(TextKey key) => () => Localizer.Text(key) + "…";
    }
}
