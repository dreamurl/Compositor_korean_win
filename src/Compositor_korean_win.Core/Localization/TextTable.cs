using System.Collections.Frozen;

namespace Compositor_korean_win.Core;

/// <summary>Everything the interface can say, one member per phrase.</summary>
public enum TextKey
{
    AppTitle,

    // Languages, each in its own words.
    LanguageEnglish,
    LanguageKorean,

    // Tools.
    ToolMove,
    ToolRectangleMarquee,
    ToolEllipseMarquee,
    ToolLasso,
    ToolPolygonLasso,
    ToolBrush,
    ToolEraser,
    ToolCloneStamp,
    ToolBlur,
    ToolHeal,
    ToolMagicWand,
    ToolGradient,
    ToolShape,

    // Adjustments, as the Image menu and the adjustment layers name them.
    AdjustLevels,
    AdjustCurves,
    AdjustHueSaturation,
    AdjustExposure,
    AdjustGradientMap,
    AdjustGrain,

    // Filters.
    FilterGaussianBlur,
    FilterMotionBlur,
    FilterAddNoise,
    FilterLensCorrection,

    // History steps and the names new layers are given.
    HistoryEdit,
    HistoryDuplicateLayer,
    HistoryMoveLayer,
    HistoryTransformLayer,
    HistoryMoveSelection,
    HistoryDuplicateSelection,
    HistoryAdjustmentLayer,
    LayerCopyName,

    // The menu bar. An ampersand marks the letter Alt reaches the item by.
    MenuFile,
    MenuEdit,
    MenuImage,
    MenuLayer,
    MenuSelect,
    MenuFilter,
    MenuView,
    MenuHelp,
    MenuAdjustments,
    MenuNewAdjustmentLayer,
    MenuPreferences,
    MenuLanguage,

    CommandOpen,
    CommandSave,
    CommandSaveAs,
    CommandExportPng,
    CommandExit,
    CommandUndo,
    CommandUndoNamed,
    CommandRedo,
    CommandRedoNamed,
    CommandSelectAll,
    CommandDeselect,
    CommandDuplicateLayer,
    CommandZoomIn,
    CommandZoomOut,
    CommandFitOnScreen,
    CommandActualPixels,
    CommandAbout,

    // The Layer menu.
    LayerNameNumbered,
    FolderNameNumbered,
    CommandNewLayer,
    CommandDeleteLayer,
    CommandMoveLayerUp,
    CommandMoveLayerDown,
    CommandGroupLayers,
    CommandMoveOutOfGroup,
    CommandShowLayer,
    CommandHideLayer,
    CommandCreateClippingMask,
    CommandReleaseClippingMask,
    CommandMergeDown,
    CommandMergeLayers,
    CommandMergeGroup,
    CommandFlipLayerHorizontal,
    CommandFlipLayerVertical,
    CommandFlipCanvasHorizontal,
    CommandFlipCanvasVertical,

    // Files and what can go wrong with them.
    DocumentUntitled,
    FileTypeProject,
    FileTypeImages,
    FileTypePng,
    ErrorCannotOpen,
    ErrorCannotSave,
    ErrorProjectInvalid,
    ErrorProjectVersion,
    ErrorProjectMissingImage,
    ErrorProjectTooLarge,
    ErrorProjectEncode,
    ErrorImageUnreadable,
    AboutText,
}

/// <summary>
/// The words, each phrase in both languages on one line.
/// </summary>
/// <remarks>
/// <para>
/// The Korean follows the Korean edition of Photoshop wherever it has a word for the thing —
/// 혼합 모드, 그레이디언트, 자동 선택 도구, 스팟 복구 브러시 — since that is the vocabulary the
/// people this is for already know (docs/windows-port.md §6).
/// </para>
/// <para>
/// This file is the only one in the source tree allowed to hold Korean, and a test holds it to
/// that: a Korean phrase anywhere else is a phrase the English interface would show untranslated.
/// </para>
/// </remarks>
public static class TextTable
{
    public static readonly (TextKey Key, string English, string Korean)[] Entries =
    [
        (TextKey.AppTitle, "Compositor", "Compositor 한국어판"),

        (TextKey.LanguageEnglish, "English", "English"),
        (TextKey.LanguageKorean, "한국어", "한국어"),

        (TextKey.ToolMove, "Move Tool", "이동 도구"),
        (TextKey.ToolRectangleMarquee, "Rectangular Marquee Tool", "사각형 선택 윤곽 도구"),
        (TextKey.ToolEllipseMarquee, "Elliptical Marquee Tool", "원형 선택 윤곽 도구"),
        (TextKey.ToolLasso, "Lasso Tool", "올가미 도구"),
        (TextKey.ToolPolygonLasso, "Polygonal Lasso Tool", "다각형 올가미 도구"),
        (TextKey.ToolBrush, "Brush Tool", "브러시 도구"),
        (TextKey.ToolEraser, "Eraser Tool", "지우개 도구"),
        (TextKey.ToolCloneStamp, "Clone Stamp Tool", "복제 도장 도구"),
        (TextKey.ToolBlur, "Blur Tool", "흐림 효과 도구"),
        (TextKey.ToolHeal, "Spot Healing Brush Tool", "스팟 복구 브러시 도구"),
        (TextKey.ToolMagicWand, "Magic Wand Tool", "자동 선택 도구"),
        (TextKey.ToolGradient, "Gradient Tool", "그레이디언트 도구"),
        (TextKey.ToolShape, "Shape Tool", "모양 도구"),

        (TextKey.AdjustLevels, "Levels", "레벨"),
        (TextKey.AdjustCurves, "Curves", "곡선"),
        (TextKey.AdjustHueSaturation, "Hue/Saturation", "색조/채도"),
        (TextKey.AdjustExposure, "Exposure", "노출"),
        (TextKey.AdjustGradientMap, "Gradient Map", "그레이디언트 맵"),
        (TextKey.AdjustGrain, "Grain", "그레인"),

        (TextKey.FilterGaussianBlur, "Gaussian Blur", "가우시안 흐림 효과"),
        (TextKey.FilterMotionBlur, "Motion Blur", "동작 흐림 효과"),
        (TextKey.FilterAddNoise, "Add Noise", "노이즈 추가"),
        (TextKey.FilterLensCorrection, "Lens Correction", "렌즈 교정"),

        (TextKey.HistoryEdit, "Edit", "편집"),
        (TextKey.HistoryDuplicateLayer, "Duplicate Layer", "레이어 복제"),
        (TextKey.HistoryMoveLayer, "Move", "이동"),
        (TextKey.HistoryTransformLayer, "Transform", "변형"),
        (TextKey.HistoryMoveSelection, "Move Selection", "선택 영역 이동"),
        (TextKey.HistoryDuplicateSelection, "Duplicate Selection", "선택 영역 복제"),
        (TextKey.HistoryAdjustmentLayer, "New {0} Layer", "새 {0} 레이어"),
        (TextKey.LayerCopyName, "{0} copy", "{0} 복사"),

        (TextKey.MenuFile, "&File", "파일(&F)"),
        (TextKey.MenuEdit, "&Edit", "편집(&E)"),
        (TextKey.MenuImage, "&Image", "이미지(&I)"),
        (TextKey.MenuLayer, "&Layer", "레이어(&L)"),
        (TextKey.MenuSelect, "&Select", "선택(&S)"),
        (TextKey.MenuFilter, "Fil&ter", "필터(&T)"),
        (TextKey.MenuView, "&View", "보기(&V)"),
        (TextKey.MenuHelp, "&Help", "도움말(&H)"),
        (TextKey.MenuAdjustments, "&Adjustments", "조정(&J)"),
        (TextKey.MenuNewAdjustmentLayer, "New Ad&justment Layer", "새 조정 레이어(&J)"),
        (TextKey.MenuPreferences, "Pre&ferences", "환경 설정(&N)"),
        (TextKey.MenuLanguage, "&Language", "언어(&L)"),

        (TextKey.CommandOpen, "&Open…", "열기(&O)…"),
        (TextKey.CommandSave, "&Save", "저장(&S)"),
        (TextKey.CommandSaveAs, "Save &As…", "다른 이름으로 저장(&A)…"),
        (TextKey.CommandExportPng, "Export as &PNG…", "PNG로 내보내기(&P)…"),
        (TextKey.CommandExit, "E&xit", "끝내기(&X)"),
        (TextKey.CommandUndo, "&Undo", "실행 취소(&U)"),
        (TextKey.CommandUndoNamed, "&Undo {0}", "{0} 실행 취소(&U)"),
        (TextKey.CommandRedo, "&Redo", "다시 실행(&R)"),
        (TextKey.CommandRedoNamed, "&Redo {0}", "{0} 다시 실행(&R)"),
        (TextKey.CommandSelectAll, "&All", "모두(&A)"),
        (TextKey.CommandDeselect, "&Deselect", "선택 해제(&D)"),
        (TextKey.CommandDuplicateLayer, "&Duplicate Layer", "레이어 복제(&D)"),
        (TextKey.CommandZoomIn, "Zoom &In", "확대(&I)"),
        (TextKey.CommandZoomOut, "Zoom &Out", "축소(&O)"),
        (TextKey.CommandFitOnScreen, "&Fit on Screen", "화면 크기에 맞추기(&F)"),
        (TextKey.CommandActualPixels, "&Actual Pixels", "실제 픽셀(&A)"),
        (TextKey.CommandAbout, "&About Compositor", "Compositor 정보(&A)"),

        (TextKey.LayerNameNumbered, "Layer {0}", "레이어 {0}"),
        (TextKey.FolderNameNumbered, "Group {0}", "그룹 {0}"),
        (TextKey.CommandNewLayer, "&New Layer", "새 레이어(&N)"),
        (TextKey.CommandDeleteLayer, "De&lete Layer", "레이어 삭제(&L)"),
        (TextKey.CommandMoveLayerUp, "Bring For&ward", "앞으로 가져오기(&W)"),
        (TextKey.CommandMoveLayerDown, "Send Bac&kward", "뒤로 보내기(&K)"),
        (TextKey.CommandGroupLayers, "&Group Layers", "레이어 그룹화(&G)"),
        (TextKey.CommandMoveOutOfGroup, "Move &Out of Group", "그룹에서 꺼내기(&O)"),
        (TextKey.CommandShowLayer, "&Show Layer", "레이어 표시(&S)"),
        (TextKey.CommandHideLayer, "&Hide Layer", "레이어 숨기기(&H)"),
        (TextKey.CommandCreateClippingMask, "Create &Clipping Mask", "클리핑 마스크 만들기(&C)"),
        (TextKey.CommandReleaseClippingMask, "Release &Clipping Mask", "클리핑 마스크 해제(&C)"),
        (TextKey.CommandMergeDown, "&Merge Down", "아래로 병합(&E)"),
        (TextKey.CommandMergeLayers, "&Merge Layers", "레이어 병합(&E)"),
        (TextKey.CommandMergeGroup, "&Merge Group", "그룹 병합(&E)"),
        (TextKey.CommandFlipLayerHorizontal, "Flip Layer &Horizontal", "레이어 가로로 뒤집기(&H)"),
        (TextKey.CommandFlipLayerVertical, "Flip Layer &Vertical", "레이어 세로로 뒤집기(&V)"),
        (TextKey.CommandFlipCanvasHorizontal, "Flip Canvas &Horizontal", "캔버스 가로로 뒤집기(&H)"),
        (TextKey.CommandFlipCanvasVertical, "Flip Canvas &Vertical", "캔버스 세로로 뒤집기(&V)"),

        (TextKey.DocumentUntitled, "Untitled", "제목 없음"),
        (TextKey.FileTypeProject, "Compositor project", "Compositor 프로젝트"),
        (TextKey.FileTypeImages, "Images", "이미지"),
        (TextKey.FileTypePng, "PNG image", "PNG 이미지"),
        (TextKey.ErrorCannotOpen, "{0} could not be opened.", "{0}을(를) 열 수 없습니다."),
        (TextKey.ErrorCannotSave, "{0} could not be saved.", "{0}을(를) 저장할 수 없습니다."),
        (TextKey.ErrorProjectInvalid, "It is not a Compositor project, or it is damaged.",
                                      "Compositor 프로젝트가 아니거나 손상되었습니다."),
        (TextKey.ErrorProjectVersion, "It was saved in a newer format ({0}) than this version reads.",
                                      "이 버전이 읽지 못하는 새 형식({0})으로 저장된 프로젝트입니다."),
        (TextKey.ErrorProjectMissingImage, "An image it refers to is missing.",
                                           "프로젝트가 가리키는 이미지가 없습니다."),
        (TextKey.ErrorProjectTooLarge, "It is larger than a project may be.",
                                       "프로젝트가 허용되는 크기를 넘습니다."),
        (TextKey.ErrorProjectEncode, "Its pixels could not be written.", "픽셀을 기록하지 못했습니다."),
        (TextKey.ErrorImageUnreadable, "The image could not be read.", "이미지를 읽을 수 없습니다."),
        (TextKey.AboutText,
            "Compositor for Windows\n\nA reimplementation of Compositor by Wonder Assembly LLC, "
            + "used under the MIT License.",
            "Compositor 윈도우판\n\nWonder Assembly LLC의 Compositor를 MIT 라이선스에 따라 "
            + "다시 구현한 프로그램입니다."),
    ];

    /// <summary>Phrases that are the same in both languages on purpose — names, not words.</summary>
    public static readonly FrozenSet<TextKey> Untranslated =
        new[] { TextKey.LanguageEnglish, TextKey.LanguageKorean }.ToFrozenSet();

    private static readonly FrozenDictionary<TextKey, (string English, string Korean)> s_byKey =
        Entries.ToFrozenDictionary(entry => entry.Key, entry => (entry.English, entry.Korean));

    /// <summary>
    /// The phrase for a key, or the key's own name when the table has no row for it — which the
    /// tests make sure never ships.
    /// </summary>
    public static string Lookup(TextKey key, Language language) =>
        s_byKey.TryGetValue(key, out (string English, string Korean) row)
            ? language == Language.Korean ? row.Korean : row.English
            : key.ToString();
}
