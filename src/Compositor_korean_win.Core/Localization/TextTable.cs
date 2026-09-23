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
