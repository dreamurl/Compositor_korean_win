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
    CommandImportImages,

    // Panels, the options bar and the status bar.
    PanelLayers,
    BlendNormal,
    BlendMultiply,
    BlendScreen,
    BlendOverlay,
    BlendDarken,
    BlendLighten,
    BlendDifference,
    BlendColorDodge,
    BlendColorBurn,
    BlendHue,
    BlendSaturation,
    BlendColor,
    BlendLuminosity,
    LabelBlendMode,
    LabelOpacity,
    LabelSize,
    LabelHardness,
    LabelStrength,
    LabelTolerance,
    LabelContiguous,
    LabelAligned,
    LabelAntiAlias,
    LabelSelectionMode,
    SelectionReplace,
    SelectionAdd,
    SelectionSubtract,
    LabelSampleSize,
    SamplePoint,
    Sample3By3,
    Sample5By5,
    LabelAllLayers,
    LabelAutoSelect,
    LabelSelectionStep,
    LabelType,
    LabelCornerRadius,
    LabelWidth,
    LabelHeight,
    LabelAngle,
    HealContentAware,
    HealCreateTexture,
    HealProximityMatch,
    GradientLinear,
    GradientRadial,
    GradientForegroundBackground,
    GradientForegroundTransparent,
    ButtonApply,
    ShapeRectangle,
    ShapeEllipse,
    TooltipForeground,
    TooltipBackground,
    TooltipSwapColors,
    TooltipDefaultColors,
    TooltipVisibility,
    TooltipNewAdjustmentLayer,
    UnitPixels,
    UnitPercent,
    StatusDocumentSize,
    StatusNoDocument,
    WelcomeTitle,
    HistoryOpacity,
    HistoryBlendMode,
    HistoryRenameLayer,
    CommandExportJpeg,
    CommandClose,
    FileTypeJpeg,
    PromptSaveChanges,

    // The Layer menu.
    LayerNameNumbered,
    LayerBackground,
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

    // The Select menu, pixels, the clipboard and the layer mask.
    CommandInverse,
    CommandSelectLayerPixels,
    CommandExpandSelection,
    CommandContractSelection,
    CommandInvert,
    CommandFillForeground,
    CommandFillBackground,
    CommandClear,
    CommandContentAwareFill,
    CommandLayerViaCopy,
    CommandLayerViaCut,
    CommandCut,
    CommandCopy,
    CommandCopyMerged,
    CommandPaste,
    MenuLayerMask,
    CommandAddLayerMask,
    CommandDeleteLayerMask,
    CommandDisableLayerMask,
    CommandEnableLayerMask,

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

    // Sheets: their buttons, and the adjustments' and filters' settings.
    DialogOk,
    DialogCancel,
    DialogReset,
    LabelPreview,
    LabelRadius,
    LabelDistance,
    LabelAmount,
    NoiseUniform,
    NoiseGaussian,
    LabelMonochromatic,
    LabelRemoveDistortion,
    NoteLensDistortion,
    LabelExposure,
    LabelOffset,
    LabelGamma,
    LabelRoughness,
    LabelHue,
    LabelSaturation,
    LabelLightness,
    LabelColorize,
    NoteLimitedToSelection,
    UnitPx,
    HistoryEditAdjustmentLayer,

    // Levels.
    ChannelRgb,
    ChannelRed,
    ChannelGreen,
    ChannelBlue,
    LabelInputBlack,
    LabelInputWhite,
    LabelOutputBlack,
    LabelOutputWhite,
    LabelSample,
    SampleBlack,
    SampleGray,
    SampleWhite,
    NoteSampleBlack,
    NoteSampleGray,
    NoteSampleWhite,
    LabelAuto,
    AutoContrast,
    AutoColor,
    AutoNeutral,
    NoteHistogramOwn,
    NoteHistogramBelow,

    // Hue/Saturation's ranges, Curves, Gradient Map and the colour picker.
    RangeMaster,
    RangeReds,
    RangeYellows,
    RangeGreens,
    RangeCyans,
    RangeBlues,
    RangeMagentas,
    HueSample,
    HueAddToSample,
    HueSubtractFromSample,
    HueTargeted,
    NoteHueSample,
    NoteHueAddToSample,
    NoteHueSubtractFromSample,
    NoteHueTargeted,
    LabelInvertRange,
    NoteCurvesHelp,
    LabelCurveInput,
    LabelCurveOutput,
    CurvesRemovePoint,
    CurvesResetCurve,
    LabelShadows,
    LabelHighlights,
    LabelReverse,
    NoteColourPickerSample,
    LabelNewColour,
    LabelCurrentColour,
    LabelR,
    LabelG,
    LabelB,

    // The document's sheets — New, Canvas Size, Image Size, Export JPEG — and the options bar's transform.
    CommandNew,
    CommandCanvasSize,
    CommandImageSize,
    SheetNewCanvas,
    SheetCanvasSize,
    SheetImageSize,
    SheetExportJpeg,
    SheetWidth,
    SheetHeight,
    DialogCreate,
    DialogExport,
    UnitPixelsName,
    UnitPercentName,
    LabelRelative,
    LabelLockAspect,
    NoteCurrentSize,
    NoteNewSize,
    NoteSizeLimits,
    NoteNewCanvas,
    LabelAnchor,
    LabelCanvasExtension,
    ExtensionTransparent,
    ExtensionForeground,
    ExtensionBackground,
    ExtensionWhite,
    ExtensionBlack,
    LayerCanvasExtension,
    LabelResolution,
    UnitPixelsPerInch,
    LabelResample,
    NoteResample,
    NoteNoResample,
    LabelQuality,
    LabelMatte,
    NoteJpegSize,
    LabelScale,
    TooltipLockRatio,
    ButtonFlipHorizontal,
    ButtonFlipVertical,

    // Tabs.
    CommandNextDocument,
    CommandPreviousDocument,
    TooltipCloseTab,

    // Painting a layer mask, Smudge and Liquify, and the clone stamp over every layer.
    CommandInvertMask,
    CommandSelectMask,
    ToolSmudge,
    ToolLiquify,
    BlurModeBlur,
    LabelSampleAllLayers,
    LabelEditingMask,
    TooltipLayerMask,
    NoteMaskTool,

    // Linking a mask to its layer, moving it on its own, and copying it to another layer.
    CommandLinkMask,
    CommandUnlinkMask,
    HistoryTransformMask,
    HistoryCopyMask,
    HistoryReplaceMask,
    TooltipMaskLink,
    TooltipMaskUnlinked,

    // Remove Background, the one command that needs the AI build.
    FilterRemoveBackground,
    CommandRemoveBackgroundUnavailable,
    NoteRemoveBackground,
    NoteFindingSubject,
    NoteNoSubject,
    NoteAiUnavailable,
    NoteAiFailed,
    QualityBasic,
    QualityAdvanced,
    LabelRefine,
    LabelMatteContrast,
    LabelShiftEdge,

    // The version, and Help > Check for Updates.
    AboutVersion,
    CommandCheckUpdates,
    CommandCopyAiGuide,
    CommandEditAiRules,
    AiRulesDefault,
    AiGuideCopied,
    AiGuideIntro,
    AiGuideAsk,
    AiShellGuideTitle,
    AiShellGuideEdits,
    AiShellGuideUsage,
    AiShellGuideResult,
    AiShellGuidePipe,
    AiShellGuideOverview,
    AiShellGuideRules,
    AiShellGuideTroubleshooting,
    AiShellGuideTools,
    AiWorkflow,
    AiTopicReproduce,
    AiTopicDesign,
    AiTopicRetouch,
    UpdateAvailable,
    UpdateLatest,
    UpdateFailed,
    UpdateUnreadable,

    // The Crop tool.
    ToolCrop,
    LabelAspectRatio,
    CropRatioFree,
    CropRatioOriginal,
    NoteFrameSize,
    ButtonApplyCrop,

    // Layer > Transform (Ctrl+T): floating selected pixels.
    CommandTransformSelection,
    CommandTransformLayer,
    LayerFloatingSelection,
    NoteFloating,

    // View > Pixel Grid, View > Show Transform Controls, Layer > Edit Adjustment.
    CommandPixelGrid,
    CommandTransformControls,
    CommandEditAdjustment,

    // The Layers panel: dragging rows, the row menu, the hide-all mask.
    HistoryArrangeLayers,
    CommandAddHideMask,
    CommandRenameLayer,

    // The Eyedropper, Hand and Zoom tools.
    ToolEyedropper,
    ToolHand,
    ToolZoom,

    // No tool (A), and the Eyedropper's one option.
    ToolIdle,
    LabelSampleRing,

    // The Type tool, Warp Text, the added shapes and Layer Style.
    ToolText,
    LabelFont,
    LabelBold,
    LabelItalic,
    AlignLeft,
    AlignCenter,
    AlignRight,
    LabelTracking,
    LabelLeading,
    ButtonWarp,
    TitleWarpText,
    LabelStyle,
    LabelBend,
    LabelHorizontalDistortion,
    LabelVerticalDistortion,
    WarpNone,
    WarpArc,
    WarpArcLower,
    WarpArcUpper,
    WarpArch,
    WarpBulge,
    WarpShellLower,
    WarpShellUpper,
    WarpFlag,
    WarpWave,
    WarpFish,
    WarpRise,
    WarpFisheye,
    WarpInflate,
    WarpSqueeze,
    WarpTwist,
    NoteTypeHint,
    ShapePolygon,
    ShapeStar,
    ShapeLine,
    LabelSides,
    LabelPoints,
    LabelInset,
    LabelCurved,
    LabelFill,
    LabelStroke,
    LabelLineWidth,
    HistoryAddText,
    HistoryEditText,
    HistoryTextStyle,
    CommandRasterizeType,
    CommandLayerStyle,
    HistoryLayerStyle,
    EffectShadow,
    EffectGlow,
    EffectStroke,
    LabelSpread,
    LabelPosition,
    StrokeOutside,
    StrokeInside,
    StrokeCenter,
    LabelColour,

    // PSD: open, save and export, and what could not cross.
    FileTypePsd,
    FileTypeSupported,
    CommandExportPsd,
    ErrorPsdUnreadable,
    PsdNotesOpened,
    PsdNotesSaved,
    PsdNoteBlendModeReplaced,
    PsdNoteAdjustmentDropped,
    PsdNoteGradientMapSimplified,
    PsdNoteEffectDropped,
    PsdNoteEffectSimplified,
    PsdNoteTypeRasterized,
    PsdNoteSmartObjectRasterized,
    PsdNoteVectorMaskDropped,
    PsdNoteMaskParametersDropped,
    PsdNoteGroupMerged,
    PsdNoteClippingDropped,
    PsdNoteFlattenedOnly,
    PsdNoteTextExportedAsPixels,
    PsdNoteGrainDropped,
    PsdNoteClippingBaked,
    PsdNoteAdjustmentSimplified,

    // Filter › Distort.
    MenuDistort,
    FilterPinch,
    FilterSpherize,
    FilterTwirl,
    FilterWave,
    FilterPolarCoordinates,
    LabelWavelength,
    LabelAmplitude,
    PolarFromRectangular,
    PolarToRectangular,

    // Edit › Transform's modes, the warp grid, and Filter › Liquify.
    MenuTransform,
    CommandTransformScale,
    CommandTransformRotate,
    CommandTransformSkew,
    CommandTransformDistort,
    CommandTransformPerspective,
    CommandTransformWarp,
    CommandRotate180,
    CommandRotateClockwise,
    CommandRotateCounterclockwise,
    HistoryWarp,
    CommandLiquify,
    HistoryLiquify,
    LiquifyForward,
    LiquifyReconstruct,
    LiquifyTwirl,
    LiquifyPucker,
    LiquifyBloat,
    LiquifyPushLeft,
    LiquifyFreeze,
    LiquifyThaw,
    LabelPressure,
    ButtonRestoreAll,
    WarpCustom,
    LabelTransformMode,
    TransformModeFree,
    NoteWarpGrid,
    NoteLiquify,

    // PSD type opened as editable text.
    PsdNoteTypeSimplified,
    PsdNoteFontMissing,
    PsdMissingFontNames,
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
        (TextKey.PanelLayers, "Layers", "레이어"),
        (TextKey.BlendNormal, "Normal", "표준"),
        (TextKey.BlendMultiply, "Multiply", "곱하기"),
        (TextKey.BlendScreen, "Screen", "스크린"),
        (TextKey.BlendOverlay, "Overlay", "오버레이"),
        (TextKey.BlendDarken, "Darken", "어둡게 하기"),
        (TextKey.BlendLighten, "Lighten", "밝게 하기"),
        (TextKey.BlendDifference, "Difference", "차이"),
        (TextKey.BlendColorDodge, "Color Dodge", "색상 닷지"),
        (TextKey.BlendColorBurn, "Color Burn", "색상 번"),
        (TextKey.BlendHue, "Hue", "색조"),
        (TextKey.BlendSaturation, "Saturation", "채도"),
        (TextKey.BlendColor, "Color", "색상"),
        (TextKey.BlendLuminosity, "Luminosity", "광도"),
        (TextKey.LabelBlendMode, "Blend Mode", "혼합 모드"),
        (TextKey.LabelOpacity, "Opacity", "불투명도"),
        (TextKey.LabelSize, "Size", "크기"),
        (TextKey.LabelHardness, "Hardness", "경도"),
        (TextKey.LabelStrength, "Strength", "강도"),
        (TextKey.LabelTolerance, "Tolerance", "허용치"),
        (TextKey.LabelContiguous, "Contiguous", "인접"),
        (TextKey.LabelAligned, "Aligned", "정렬"),
        (TextKey.LabelAntiAlias, "Anti-alias", "앤티 앨리어스"),
        (TextKey.LabelSelectionStep, "Expand/Contract", "확장/축소"),
        (TextKey.LabelType, "Type", "유형"),
        (TextKey.LabelCornerRadius, "Corner Radius", "모퉁이 반경"),
        (TextKey.LabelWidth, "W", "너비"),
        (TextKey.LabelHeight, "H", "높이"),
        (TextKey.LabelAngle, "Angle", "각도"),
        (TextKey.HealContentAware, "Content-Aware", "내용 인식"),
        (TextKey.HealCreateTexture, "Create Texture", "텍스처 만들기"),
        (TextKey.HealProximityMatch, "Proximity Match", "근접 일치"),
        (TextKey.GradientLinear, "Linear", "선형"),
        (TextKey.GradientRadial, "Radial", "방사형"),
        (TextKey.GradientForegroundBackground, "Foreground to Background", "전경색에서 배경색"),
        (TextKey.GradientForegroundTransparent, "Foreground to Transparent", "전경색에서 투명"),
        (TextKey.ButtonApply, "Apply", "적용"),
        (TextKey.ShapeRectangle, "Rectangle", "사각형"),
        (TextKey.ShapeEllipse, "Ellipse", "타원"),
        (TextKey.TooltipForeground, "Foreground Color", "전경색"),
        (TextKey.TooltipBackground, "Background Color", "배경색"),
        (TextKey.TooltipSwapColors, "Swap Colors (X)", "색상 바꾸기 (X)"),
        (TextKey.TooltipDefaultColors, "Default Colors (D)", "기본 색상 (D)"),
        (TextKey.TooltipVisibility, "Show or Hide", "표시/숨기기"),
        (TextKey.TooltipNewAdjustmentLayer, "New Adjustment Layer", "새 조정 레이어"),
        (TextKey.UnitPixels, "{0} px", "{0}픽셀"),
        (TextKey.UnitPercent, "{0}%", "{0}%"),
        (TextKey.StatusDocumentSize, "{0} × {1} px", "{0} × {1}픽셀"),
        (TextKey.StatusNoDocument, "No document", "문서 없음"),
        (TextKey.WelcomeTitle, "Open an image or a project to begin.", "이미지나 프로젝트를 열어 시작하세요."),
        (TextKey.HistoryOpacity, "Opacity", "불투명도"),
        (TextKey.HistoryBlendMode, "Blend Mode", "혼합 모드"),
        (TextKey.HistoryRenameLayer, "Rename Layer", "레이어 이름 바꾸기"),
        (TextKey.CommandImportImages, "&Import Images…", "이미지 가져오기(&I)…"),
        (TextKey.CommandExportJpeg, "Export as &JPEG…", "JPEG로 내보내기(&J)…"),
        (TextKey.CommandClose, "&Close", "닫기(&C)"),
        (TextKey.FileTypeJpeg, "JPEG image", "JPEG 이미지"),
        (TextKey.PromptSaveChanges, "Save the changes to {0} before closing it?",
                                    "{0}을(를) 닫기 전에 변경 사항을 저장하시겠습니까?"),

        (TextKey.LayerNameNumbered, "Layer {0}", "레이어 {0}"),
        (TextKey.LayerBackground, "Background", "배경"),
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

        (TextKey.CommandInverse, "&Inverse", "반전(&I)"),
        (TextKey.CommandSelectLayerPixels, "&Layer Pixels", "레이어 픽셀(&L)"),
        (TextKey.CommandExpandSelection, "&Expand by {0} px", "{0}픽셀 확장(&E)"),
        (TextKey.CommandContractSelection, "&Contract by {0} px", "{0}픽셀 축소(&C)"),
        (TextKey.CommandInvert, "In&vert", "색상 반전(&V)"),
        (TextKey.CommandFillForeground, "Fill with &Foreground Color", "전경색으로 칠하기(&F)"),
        (TextKey.CommandFillBackground, "Fill with &Background Color", "배경색으로 칠하기(&B)"),
        (TextKey.CommandClear, "Cl&ear", "지우기(&E)"),
        (TextKey.CommandContentAwareFill, "Content-A&ware Fill", "내용 인식 채우기(&W)"),
        (TextKey.CommandLayerViaCopy, "Layer via &Copy", "복사한 레이어(&C)"),
        (TextKey.CommandLayerViaCut, "Layer via Cu&t", "잘라낸 레이어(&T)"),
        (TextKey.CommandCut, "Cu&t", "잘라내기(&T)"),
        (TextKey.CommandCopy, "&Copy", "복사(&C)"),
        (TextKey.CommandCopyMerged, "Copy Mer&ged", "병합하여 복사(&Y)"),
        (TextKey.CommandPaste, "&Paste", "붙여넣기(&P)"),
        (TextKey.MenuLayerMask, "Layer &Mask", "레이어 마스크(&M)"),
        (TextKey.CommandAddLayerMask, "&Add Layer Mask", "레이어 마스크 추가(&A)"),
        (TextKey.CommandDeleteLayerMask, "&Delete Layer Mask", "레이어 마스크 삭제(&D)"),
        (TextKey.CommandDisableLayerMask, "Disa&ble Layer Mask", "레이어 마스크 사용 안 함(&B)"),
        (TextKey.CommandEnableLayerMask, "Ena&ble Layer Mask", "레이어 마스크 사용(&B)"),

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
        (TextKey.DialogOk, "OK", "확인"),
        (TextKey.DialogCancel, "Cancel", "취소"),
        (TextKey.DialogReset, "Reset", "재설정"),
        (TextKey.LabelPreview, "Preview", "미리 보기"),
        (TextKey.LabelRadius, "Radius", "반경"),
        (TextKey.LabelDistance, "Distance", "거리"),
        (TextKey.LabelAmount, "Amount", "양"),
        (TextKey.NoiseUniform, "Uniform", "균일"),
        (TextKey.NoiseGaussian, "Gaussian", "가우시안"),
        (TextKey.LabelMonochromatic, "Monochromatic", "단색"),
        (TextKey.LabelRemoveDistortion, "Remove Distortion", "왜곡 제거"),
        (TextKey.NoteLensDistortion, "Positive straightens lines that bow outward (barrel); negative, lines that bow inward (pincushion).", "양수는 바깥으로 휜 선(술통형)을, 음수는 안쪽으로 휜 선(실패형)을 곧게 폅니다."),
        (TextKey.LabelExposure, "Exposure", "노출"),
        (TextKey.LabelOffset, "Offset", "오프셋"),
        (TextKey.LabelGamma, "Gamma", "감마"),
        (TextKey.LabelRoughness, "Roughness", "거칠기"),
        (TextKey.LabelHue, "Hue", "색조"),
        (TextKey.LabelSaturation, "Saturation", "채도"),
        (TextKey.LabelLightness, "Lightness", "밝기"),
        (TextKey.LabelColorize, "Colorize", "색상화"),
        (TextKey.NoteLimitedToSelection, "Limited to the selection", "선택 영역에만 적용됩니다"),
        (TextKey.UnitPx, "px", "픽셀"),
        (TextKey.HistoryEditAdjustmentLayer, "Edit {0} Layer", "{0} 레이어 편집"),
        (TextKey.ChannelRgb, "RGB", "RGB"),
        (TextKey.ChannelRed, "Red", "빨강"),
        (TextKey.ChannelGreen, "Green", "녹색"),
        (TextKey.ChannelBlue, "Blue", "파랑"),
        (TextKey.LabelInputBlack, "Input Black", "입력 검정"),
        (TextKey.LabelInputWhite, "Input White", "입력 흰색"),
        (TextKey.LabelOutputBlack, "Output Black", "출력 검정"),
        (TextKey.LabelOutputWhite, "Output White", "출력 흰색"),
        (TextKey.LabelSample, "Sample", "샘플"),
        (TextKey.SampleBlack, "Black", "검정"),
        (TextKey.SampleGray, "Gray", "회색"),
        (TextKey.SampleWhite, "White", "흰색"),
        (TextKey.NoteSampleBlack, "Click the image to set the black point. Click the eyedropper again to stop.", "이미지를 클릭해 검정 기준점을 정하세요. 스포이드를 다시 누르면 멈춥니다."),
        (TextKey.NoteSampleGray, "Click the image to set the gray point. Click the eyedropper again to stop.", "이미지를 클릭해 회색 기준점을 정하세요. 스포이드를 다시 누르면 멈춥니다."),
        (TextKey.NoteSampleWhite, "Click the image to set the white point. Click the eyedropper again to stop.", "이미지를 클릭해 흰색 기준점을 정하세요. 스포이드를 다시 누르면 멈춥니다."),
        (TextKey.LabelAuto, "Auto", "자동"),
        (TextKey.AutoContrast, "Contrast", "대비"),
        (TextKey.AutoColor, "Color", "색상"),
        (TextKey.AutoNeutral, "Color + Neutral Midtones", "색상 + 중간톤 중립"),
        (TextKey.NoteHistogramOwn, "Histogram of the layer's own pixels, weighted by their opacity.", "레이어 자체 픽셀의 히스토그램입니다(불투명도로 가중)."),
        (TextKey.NoteHistogramBelow, "Histogram of the layers below, which is what this adjustment layer changes.", "이 조정 레이어가 바꾸는 아래 레이어들의 히스토그램입니다."),
        (TextKey.RangeMaster, "Master", "마스터"),
        (TextKey.RangeReds, "Reds", "빨강 계열"),
        (TextKey.RangeYellows, "Yellows", "노랑 계열"),
        (TextKey.RangeGreens, "Greens", "녹색 계열"),
        (TextKey.RangeCyans, "Cyans", "녹청 계열"),
        (TextKey.RangeBlues, "Blues", "파랑 계열"),
        (TextKey.RangeMagentas, "Magentas", "마젠타 계열"),
        (TextKey.HueSample, "Sample", "샘플"),
        (TextKey.HueAddToSample, "Add to Sample", "샘플에 추가"),
        (TextKey.HueSubtractFromSample, "Subtract from Sample", "샘플에서 빼기"),
        (TextKey.HueTargeted, "Targeted Adjustment", "대상 조정"),
        (TextKey.NoteHueSample, "Click the image to center this range on that color.", "이미지를 클릭하면 이 범위를 그 색의 가운데로 옮깁니다."),
        (TextKey.NoteHueAddToSample, "Click the image to widen this range to include that color.", "이미지를 클릭하면 그 색이 들어오도록 이 범위를 넓힙니다."),
        (TextKey.NoteHueSubtractFromSample, "Click the image to narrow this range to exclude that color.", "이미지를 클릭하면 그 색이 빠지도록 이 범위를 좁힙니다."),
        (TextKey.NoteHueTargeted, "Drag on the image to change that color's saturation; hold Ctrl to change its hue.", "이미지 위를 끌면 그 색의 채도가 바뀝니다. Ctrl을 누르고 끌면 색조가 바뀝니다."),
        (TextKey.LabelInvertRange, "Apply outside this range instead", "이 범위 밖에 대신 적용"),
        (TextKey.NoteCurvesHelp, "Click to add a point. Drag to adjust.", "클릭해 점을 추가하고, 끌어서 조정하세요."),
        (TextKey.LabelCurveInput, "Input", "입력"),
        (TextKey.LabelCurveOutput, "Output", "출력"),
        (TextKey.CurvesRemovePoint, "Remove Point", "점 삭제"),
        (TextKey.CurvesResetCurve, "Reset Curve", "곡선 재설정"),
        (TextKey.LabelShadows, "Shadows", "어두운 영역"),
        (TextKey.LabelHighlights, "Highlights", "밝은 영역"),
        (TextKey.LabelReverse, "Reverse", "반전"),
        (TextKey.NoteColourPickerSample, "Click the canvas to sample a color.", "캔버스를 클릭하면 색을 추출합니다."),
        (TextKey.LabelNewColour, "New", "새 색상"),
        (TextKey.LabelCurrentColour, "Current", "현재 색상"),
        (TextKey.LabelR, "R", "R"),
        (TextKey.LabelG, "G", "G"),
        (TextKey.LabelB, "B", "B"),
        (TextKey.CommandNew, "&New…", "새로 만들기(&N)…"),
        (TextKey.CommandCanvasSize, "Canvas Si&ze…", "캔버스 크기(&Z)…"),
        (TextKey.CommandImageSize, "Image &Size…", "이미지 크기(&S)…"),
        (TextKey.SheetNewCanvas, "New Canvas", "새 캔버스"),
        (TextKey.SheetCanvasSize, "Canvas Size", "캔버스 크기"),
        (TextKey.SheetImageSize, "Image Size", "이미지 크기"),
        (TextKey.SheetExportJpeg, "Export JPEG", "JPEG로 내보내기"),
        (TextKey.SheetWidth, "Width", "너비"),
        (TextKey.SheetHeight, "Height", "높이"),
        (TextKey.DialogCreate, "Create", "만들기"),
        (TextKey.DialogExport, "Export…", "내보내기…"),
        (TextKey.UnitPixelsName, "Pixels", "픽셀"),
        (TextKey.UnitPercentName, "Percent", "퍼센트"),
        (TextKey.LabelRelative, "Relative", "상대치"),
        (TextKey.LabelLockAspect, "Lock aspect ratio", "가로세로 비율 고정"),
        (TextKey.NoteCurrentSize, "Current size: {0} × {1} px", "현재 크기: {0} × {1}픽셀"),
        (TextKey.NoteNewSize, "New size: {0} × {1} px", "새 크기: {0} × {1}픽셀"),
        (TextKey.NoteSizeLimits, "Use 1 to 30,000 pixels a side, and no more than 100 megapixels in all.", "한 변은 1–30,000픽셀, 전체는 1억 픽셀 이하로 정하세요."),
        (TextKey.NoteNewCanvas, "A transparent canvas, in sRGB.", "투명한 sRGB 캔버스입니다."),
        (TextKey.LabelAnchor, "Anchor", "기준점"),
        (TextKey.LabelCanvasExtension, "Extension color", "확장 색상"),
        (TextKey.ExtensionTransparent, "Transparent", "투명"),
        (TextKey.ExtensionForeground, "Foreground", "전경색"),
        (TextKey.ExtensionBackground, "Background", "배경색"),
        (TextKey.ExtensionWhite, "White", "흰색"),
        (TextKey.ExtensionBlack, "Black", "검정"),
        (TextKey.LayerCanvasExtension, "Canvas Extension", "캔버스 확장"),
        (TextKey.LabelResolution, "Resolution", "해상도"),
        (TextKey.UnitPixelsPerInch, "px/in", "픽셀/인치"),
        (TextKey.LabelResample, "Resample", "리샘플링"),
        (TextKey.NoteResample, "Resamples every layer's pixels. Undo brings the originals back.", "모든 레이어의 픽셀을 다시 샘플링합니다. 실행 취소하면 원래대로 돌아옵니다."),
        (TextKey.NoteNoResample, "Only the resolution changes; the pixels stay as they are.", "해상도만 바뀌고 픽셀은 그대로입니다."),
        (TextKey.LabelQuality, "Quality", "품질"),
        (TextKey.LabelMatte, "Background for transparency", "투명 영역의 배경색"),
        (TextKey.NoteJpegSize, "{0} × {1} px · {2}", "{0} × {1}픽셀 · {2}"),
        (TextKey.LabelScale, "Scale", "비율"),
        (TextKey.TooltipLockRatio, "Keep proportions", "비율 유지"),
        (TextKey.ButtonFlipHorizontal, "Flip H", "가로 뒤집기"),
        (TextKey.ButtonFlipVertical, "Flip V", "세로 뒤집기"),
        (TextKey.CommandNextDocument, "&Next Document", "다음 문서(&N)"),
        (TextKey.CommandPreviousDocument, "&Previous Document", "이전 문서(&P)"),
        (TextKey.TooltipCloseTab, "Close", "닫기"),
        (TextKey.CommandInvertMask, "In&vert Mask", "마스크 반전(&V)"),
        (TextKey.CommandSelectMask, "&Mask's Black Areas", "마스크의 검은 영역(&M)"),
        (TextKey.ToolSmudge, "Smudge", "문지르기"),
        (TextKey.ToolLiquify, "Liquify", "픽셀 유동화"),
        (TextKey.BlurModeBlur, "Blur", "흐리게"),
        (TextKey.LabelSampleAllLayers, "Sample all layers", "모든 레이어 샘플링"),
        (TextKey.LabelEditingMask, "Mask", "마스크"),
        (TextKey.TooltipLayerMask, "Layer mask — click to paint it", "레이어 마스크 — 클릭하면 마스크에 칠합니다"),
        (TextKey.NoteMaskTool, "This tool works on pixels, not on a mask.", "이 도구는 마스크가 아닌 픽셀에만 작동합니다."),
        (TextKey.CommandLinkMask, "&Link Layer Mask", "레이어 마스크 연결(&L)"),
        (TextKey.CommandUnlinkMask, "Un&link Layer Mask", "레이어 마스크 연결 해제(&L)"),
        (TextKey.HistoryTransformMask, "Transform Layer Mask", "레이어 마스크 변형"),
        (TextKey.HistoryCopyMask, "Copy Layer Mask", "레이어 마스크 복사"),
        (TextKey.HistoryReplaceMask, "Replace Layer Mask", "레이어 마스크 교체"),
        (TextKey.TooltipMaskLink, "Link the layer and its mask so they move together", "레이어와 마스크를 연결해 함께 움직입니다"),
        (TextKey.TooltipMaskUnlinked, "Unlinked: the mask moves on its own. Click to link", "연결 해제됨: 마스크가 따로 움직입니다. 클릭하면 연결합니다"),
        (TextKey.FilterRemoveBackground, "Remove Background", "배경 제거"),
        (TextKey.CommandRemoveBackgroundUnavailable, "Remove Background (AI build only)", "배경 제거 (AI 포함판 전용)"),
        (TextKey.NoteRemoveBackground, "Hide the background behind a layer mask, keeping the foreground subjects. The pixels stay, so the background can be painted back at any time.", "전경의 피사체는 남기고 배경을 레이어 마스크로 가립니다. 픽셀은 그대로 남으므로 언제든 배경을 다시 칠해 되살릴 수 있습니다."),
        (TextKey.NoteFindingSubject, "Finding the subject…", "피사체를 찾는 중…"),
        (TextKey.NoteNoSubject, "No subject was found in this layer. Try an image with a more distinct subject.", "이 레이어에서 피사체를 찾지 못했습니다. 피사체가 더 뚜렷한 이미지로 시도해 보세요."),
        (TextKey.NoteAiUnavailable, "Remove Background needs the AI build of Compositor.", "배경 제거는 AI 포함판에서 쓸 수 있습니다."),
        (TextKey.NoteAiFailed, "The model could not run: {0}", "모델을 실행하지 못했습니다: {0}"),
        (TextKey.QualityBasic, "Basic", "기본"),
        (TextKey.QualityAdvanced, "Advanced", "고급"),
        (TextKey.LabelRefine, "Refine", "다듬기"),
        (TextKey.LabelMatteContrast, "Contrast", "대비"),
        (TextKey.LabelShiftEdge, "Shift Edge", "가장자리 이동"),
        (TextKey.AboutVersion, "Version {0}", "버전 {0}"),
        (TextKey.CommandCheckUpdates, "Check for &Updates…", "업데이트 확인(&U)…"),
        (TextKey.CommandCopyAiGuide, "Copy AI &Instructions", "AI 작업 안내 복사(&I)"),
        (TextKey.CommandEditAiRules, "Edit AI &Rules…", "AI 작업 규칙 편집(&R)…"),
        (TextKey.AiRulesDefault,
            "# Rules for working in Compositor\n"
            + "\n"
            + "Edit this file to change how an AI assistant works in the editor. It is read again on every request.\n"
            + "\n"
            + "## Layer structure\n"
            + "1. Group layers by role, bottom to top: Background, Images, Shapes & decoration, Text, Adjustments. Put each new layer in its group.\n"
            + "2. Give every group and layer a meaningful name (Title, Subtitle, Logo, Background gradient). Leave no \"Layer 3\".\n"
            + "3. One element per layer. Do not combine several elements into one layer.\n"
            + "\n"
            + "## Keep it editable\n"
            + "4. Keep text as live text. Do not rasterize it; for letters styled apart use edit_text with start/end.\n"
            + "5. Do not change original images directly. Use masks, clipping, adjustment layers and effects.\n"
            + "\n"
            + "## Respect the person's work\n"
            + "6. Check the current structure with get_document before working. Ask before deleting or merging layers the person made.\n"
            + "7. Save or overwrite files only when asked.\n"
            + "\n"
            + "## How to proceed\n"
            + "8. Look at the result with render after each stage.\n"
            + "9. When done, summarize the groups and layers you made.\n"
            + "\n"
            + "## Recreating a reference\n"
            + "10. Carry the reference's material with put_pixels, colours exact and never reduced, one element per layer.\n"
            + "11. Make shapes and effects with masks, add_path, liquify, warp_layer and distort_layer. Do not paint them by hand.\n"
            + "12. Keep lettering as text layers, and fill in the picture it covered.\n"
            + "13. Compare with compare_image before saying the work is done, and report the score.",
            "# Compositor 작업 규칙\n"
            + "\n"
            + "이 파일을 고치면 AI가 편집기에서 일하는 방식이 바뀝니다. 요청마다 다시 읽으므로 저장하면 바로 적용됩니다.\n"
            + "\n"
            + "## 레이어 구조\n"
            + "1. 레이어를 역할별 그룹으로 나눈다. 아래에서 위로: 배경, 이미지, 도형·장식, 텍스트, 보정. 새 레이어는 해당 그룹 안에 만든다.\n"
            + "2. 그룹과 레이어에 의미 있는 이름을 붙인다(제목, 부제, 로고, 배경 그라디언트). \"Layer 3\" 같은 이름을 남기지 않는다.\n"
            + "3. 레이어 하나에 요소 하나. 여러 요소를 한 레이어에 합치지 않는다.\n"
            + "\n"
            + "## 수정 가능하게 유지\n"
            + "4. 텍스트는 살아 있는 텍스트로 둔다. 래스터화하지 않고, 일부 글자만 다른 스타일은 edit_text의 start/end로 준다.\n"
            + "5. 원본 이미지를 직접 고치지 않는다. 마스크, 클리핑, 조정 레이어, 효과를 쓴다.\n"
            + "\n"
            + "## 사용자 작업 존중\n"
            + "6. 작업 전에 get_document로 현재 구조를 확인한다. 사용자가 만든 레이어를 지우거나 합치기 전에 먼저 묻는다.\n"
            + "7. 파일 저장·덮어쓰기는 요청받았을 때만 한다.\n"
            + "\n"
            + "## 진행 방식\n"
            + "8. 단계마다 render로 결과를 확인한다.\n"
            + "9. 끝나면 만든 그룹과 레이어 구조를 요약해 알려 준다.\n"
            + "\n"
            + "## 레퍼런스 재현\n"
            + "10. 레퍼런스의 재료는 put_pixels로 옮긴다. 색은 원본 그대로, 줄이지 않고, 요소마다 레이어 하나.\n"
            + "11. 모양과 효과는 마스크, add_path, liquify, warp_layer, distort_layer로 만든다. 손으로 따라 칠하지 않는다.\n"
            + "12. 글자는 텍스트 레이어로 두고, 글자가 가리던 그림은 채워 넣는다.\n"
            + "13. 완성이라고 말하기 전에 compare_image로 비교하고 점수를 보고한다."),
        (TextKey.AiGuideCopied,
            "Instructions for an AI assistant are on the clipboard. Paste them into Claude, Codex or any assistant "
            + "that can run PowerShell on this PC, add what you want made, and it will work in this window while "
            + "Compositor stays open.",
            "AI에게 줄 안내를 클립보드에 복사했습니다. 이 PC에서 PowerShell을 실행할 수 있는 AI(Claude, Codex 등)에게 "
            + "붙여넣고 원하는 작업을 적어 주세요. Compositor가 켜져 있는 동안 이 창에서 바로 작업합니다."),
        (TextKey.AiGuideIntro,
            "Compositor (an image editor) is open on this PC. Run the command  compositor  in a terminal: it prints "
            + "the guide — my rules and every tool — and the same command does the work in the open window. If the "
            + "command is not found, define this PowerShell function and call  Compositor '{\"tool\":\"guide\"}'  instead.",
            "Compositor(이미지 편집기)가 이 PC에 켜져 있습니다. 터미널에서  compositor  명령을 실행하면 가이드(제 작업 "
            + "규칙과 모든 도구)가 나오고, 같은 명령으로 열린 창에서 작업할 수 있습니다. 명령을 찾을 수 없으면 아래 "
            + "PowerShell 함수를 정의하고  Compositor '{\"tool\":\"guide\"}'  로 가이드를 읽어 주세요."),
        (TextKey.AiGuideAsk,
            "Read the guide first, then do what I ask below. Look at your work with render (it prints a PNG path "
            + "to open). My request:",
            "가이드를 먼저 읽은 뒤 아래 요청대로 작업해 주세요. 결과는 render로 확인하세요(PNG 파일 경로를 "
            + "알려 줍니다). 요청:"),
        (TextKey.AiShellGuideTitle,
            "# Compositor — working in the open editor window",
            "# Compositor — 열려 있는 편집기에서 작업하기"),
        (TextKey.AiShellGuideEdits,
            "Edits appear in the window as you make them, one undo step each, and the person can keep working on them.",
            "명령으로 한 편집은 창에 즉시 나타나며 각각 실행 취소 한 단계가 됩니다. 사용자는 같은 문서에서 계속 작업할 수 있습니다."),
        (TextKey.AiShellGuideUsage,
            "Call a tool with the `compositor` command (installed with the editor, on the PATH): the tool's name, then\n"
            + "its arguments as key=value. Numbers and true/false are typed, [..] or {..} is JSON, anything else is text\n"
            + "(007 stays text). text= and prompt= are always text; in them \\n is a line break and \\\\ a backslash.\n"
            + "Quote a value with spaces. `compositor` alone prints this guide.",
            "편집기와 함께 설치되어 PATH에 등록된 `compositor` 명령 뒤에 도구 이름과 key=value 인수를 적습니다.\n"
            + "숫자와 true/false는 해당 형식으로, [..]와 {..}는 JSON으로, 나머지는 문자열로 처리됩니다(007은 문자열 그대로).\n"
            + "text=와 prompt=는 항상 문자열이며, 그 안에서 \\n은 줄바꿈, \\\\는 역슬래시 하나입니다.\n"
            + "공백이 든 값은 따옴표로 감싸세요. `compositor`만 실행하면 이 안내서를 다시 표시합니다."),
        (TextKey.AiShellGuideResult,
            "It prints the tool's answer, and for render the path of a PNG — open that file to look at the picture.\n"
            + "Tools act on the document shown in the window unless given document=...; new_document and open_document open a new tab.\n"
            + "Exit codes are 1 tool failure, 2 arguments, 3 editor start, 4 permission, 5 connection and 6 response timeout.\n"
            + "Requests wait while the person is mid-edit; the command waits up to 5 minutes. After a timeout, check with get_document before resending.\n"
            + "Read the installed version without connecting to the app with `compositor --version`.",
            "도구의 응답이 출력되며, render는 확인할 PNG 파일 경로도 출력합니다.\n"
            + "document=...를 생략하면 창에 표시된 문서를 대상으로 합니다. new_document와 open_document는 새 탭을 엽니다.\n"
            + "종료 코드는 도구 실패 1, 인수 오류 2, 편집기 시작 실패 3, 권한 거부 4, 연결 실패 5, 응답 시간 초과 6입니다.\n"
            + "사용자가 편집 중이면 요청은 끝날 때까지 기다리며, 명령은 최대 5분 기다립니다. 시간 초과 뒤에는 다시 보내기 전에 get_document로 확인하세요.\n"
            + "버전은 앱 연결 없이 `compositor --version`으로 확인할 수 있습니다."),
        (TextKey.AiShellGuidePipe,
            "Without the command, send the same requests as one JSON line to the named pipe \\\\.\\pipe\\{0} and read one line back, for example with this PowerShell function:",
            "명령을 사용할 수 없다면 같은 요청을 named pipe \\\\.\\pipe\\{0}에 JSON 한 줄로 보내고 응답 한 줄을 읽습니다. 예:"),
        (TextKey.AiShellGuideOverview,
            "Compositor is a layered image editor. Coordinates are document pixels from the top-left; colours are hex (#RRGGBB or #RRGGBBAA); opacities are 0–1. Read the structure with get_document before editing, check it often with render, and save with save_document or export_image only when asked.",
            "Compositor는 레이어 기반 이미지 편집기입니다. 좌표는 문서 왼쪽 위에서 시작하는 픽셀이며, 색상은 #RRGGBB 또는 #RRGGBBAA, 불투명도는 0–1입니다. 작업 전 get_document로 구조를 읽고, 편집 중 render로 자주 확인하며, 요청받은 경우에만 save_document나 export_image로 저장하세요."),
        (TextKey.AiShellGuideRules,
            "## Rules — set by the person who uses this editor. Follow them.",
            "## 사용자가 정한 작업 규칙 — 반드시 따르세요."),
        (TextKey.AiShellGuideTroubleshooting,
            "## Connection troubleshooting\n\n"
            + "- If access is denied, run Compositor and the command as the same Windows user and permission level.\n"
            + "- A sandbox such as Codex may require approval to access the local named pipe.\n"
            + "- If the app is running but cannot be reached, do not launch it repeatedly; check the error and exit code.",
            "## 연결 문제 해결\n\n"
            + "- 권한 거부가 나오면 Compositor와 명령을 같은 Windows 사용자·권한 수준에서 실행하세요.\n"
            + "- Codex 같은 샌드박스에서는 로컬 named pipe 접근 승인이 필요할 수 있습니다.\n"
            + "- 앱이 실행 중인데 연결되지 않으면 새 창을 반복 실행하지 말고 오류 메시지와 종료 코드를 확인하세요."),
        (TextKey.AiShellGuideTools, "## Tools (* = required)", "## 도구 (* = 필수)"),
        (TextKey.AiWorkflow,
            "## How to work\n"
            + "\n"
            + "- Plan the layers before drawing: one element per layer — material (pictures, painted areas), shapes (masks, cut-outs), textures and accents, text — named by role and grouped.\n"
            + "- A reference's material comes across exactly with `put_pixels`: every colour as it is, never reduced to fewer colours, each element on its own layer (select the element, then within_selection true).\n"
            + "- Shapes and effects are made with the editor's tools, not traced by hand: silhouettes and cut-outs with `add_path` (as mask or selection) and `set_mask`; bends and perspective with `liquify`, `warp_layer` and `distort_layer`; tone with `add_adjustment`; shadows and outlines with `set_effects`. `paint_stroke` is for touching up and filling small gaps, not for carrying pixels.\n"
            + "- Text is always a text layer (`add_text`), never pixels. Where lettering covered a picture, fill in the picture behind it.\n"
            + "- Look before you say it is done: `render` (with zoom for detail) and, against a reference, `compare_image`. Report the score and what still differs.\n"
            + "- Send long sequences as one `batch`. Very large arguments go to the pipe as JSON, not on the command line.\n"
            + "\n"
            + "Read the steps for the job before starting: `compositor guide topic=reproduce` (recreate a reference picture), `compositor guide topic=design` (make something new), `compositor guide topic=retouch` (correct a photo). Over MCP, call `guide` with the topic.",
            "## 작업 방법\n"
            + "\n"
            + "- 그리기 전에 레이어를 계획한다. 요소 하나에 레이어 하나: 재료(사진, 칠한 영역), 모양(마스크, 잘라낸 부분), 질감과 포인트, 텍스트. 역할이 드러나게 이름을 붙이고 그룹으로 묶는다.\n"
            + "- 레퍼런스의 재료는 `put_pixels`로 정확히 옮긴다. 색은 원본 그대로 두고 색 수를 줄이지 않는다. 요소마다 자기 레이어에 넣는다(요소를 선택한 뒤 within_selection true).\n"
            + "- 모양과 효과는 편집기 기능으로 만들고 손으로 따라 그리지 않는다. 실루엣과 잘라낸 틈은 `add_path`(마스크나 선택 영역으로)와 `set_mask`, 휘어짐과 원근은 `liquify`·`warp_layer`·`distort_layer`, 색조는 `add_adjustment`, 그림자와 윤곽선은 `set_effects`로 만든다. `paint_stroke`는 다듬기와 작은 빈틈 채우기에 쓰고, 픽셀을 옮기는 데 쓰지 않는다.\n"
            + "- 텍스트는 항상 텍스트 레이어(`add_text`)로 만들고 픽셀로 두지 않는다. 글자가 가리던 그림은 뒤를 채워 넣는다.\n"
            + "- 완성이라고 말하기 전에 확인한다. `render`(세부는 zoom)로 보고, 레퍼런스가 있으면 `compare_image`로 비교한다. 점수와 아직 다른 부분을 보고한다.\n"
            + "- 긴 작업은 `batch` 하나로 보낸다. 아주 큰 인수는 명령줄이 아니라 파이프에 JSON으로 보낸다.\n"
            + "\n"
            + "작업을 시작하기 전에 해당 순서를 읽는다: `compositor guide topic=reproduce`(레퍼런스 재현), `compositor guide topic=design`(새로 디자인), `compositor guide topic=retouch`(사진 보정). MCP에서는 `guide`를 topic과 함께 부른다."),
        (TextKey.AiTopicReproduce,
            "# Recreating a reference picture\n"
            + "\n"
            + "The aim is the reference's look, built the way its maker built it: its material exact and on separate layers, shaped by masks and transforms, with live text. Copying pixels is right; copying them flat onto one layer, or painting the effects by hand, is not.\n"
            + "\n"
            + "1. Look and plan. Look at the reference and list its elements: the base material (a photo, a drawing), the shapes that cut it (silhouettes, gaps), textures and accents, and the text. Plan a layer or group for each, named by role.\n"
            + "2. Canvas. `new_document` at the reference's size, so its pixels and the canvas's share coordinates.\n"
            + "3. A guide copy. `put_pixels` the whole reference onto a layer named Reference, to select from; hide it with `update_layer` visible false and delete it at the end.\n"
            + "4. Material. For each element, select it — `select` magic_wand on the Reference layer, `select` lasso, or `add_path` as=selection — then `put_pixels` with within_selection true onto the element's own layer. Colours stay exactly as they are. For the base material take everything behind the shapes, not only what shows.\n"
            + "5. What was hidden. Where text or other elements covered the material, fill it in: `select` the gap and `fill_selection` content_aware true, `paint_stroke` tool heal or clone for small areas, or `paint_stroke` tool brush in the colours around it.\n"
            + "6. Shapes. Cut-outs and silhouettes are masks, never painted white: `add_path` as=mask on the material layer (fill_rule evenodd for holes, mode subtract for gaps), or `select` then `set_mask` shape selection. Feather where the reference is soft.\n"
            + "7. Effects. Bends, swirls and perspective come from `liquify`, `warp_layer` and `distort_layer`; tone from `add_adjustment`; shadows and outlines from `set_effects`. Do not trace an effect pixel by pixel.\n"
            + "8. Text. `add_text` for every piece of lettering, matching font (`list_fonts`), size, tracking, leading, colour and position; `edit_text` with start and end for letters styled apart. Never keep the reference's lettering as pixels.\n"
            + "9. Check. `compare_image` against the reference; read the score and the worst regions, `render` them with zoom, fix, and compare again. Delete the Reference layer. Report the final score and what still differs.",
            "# 레퍼런스 재현\n"
            + "\n"
            + "목표는 레퍼런스를 만든 사람이 만든 방식 그대로 같은 모습을 만드는 것이다. 재료는 정확하게 별도 레이어로, 모양은 마스크와 변형으로, 텍스트는 살아 있는 텍스트로 만든다. 픽셀을 복사하는 것은 맞다. 한 레이어에 통째로 복사하거나 효과를 손으로 따라 칠하는 것이 틀렸다.\n"
            + "\n"
            + "1. 보고 계획한다. 레퍼런스를 보고 요소를 나눈다: 바탕 재료(사진, 그림), 그것을 잘라내는 모양(실루엣, 틈), 질감과 포인트, 텍스트. 요소마다 레이어나 그룹을 계획하고 역할로 이름을 붙인다.\n"
            + "2. 캔버스. `new_document`를 레퍼런스 크기로 만들어 레퍼런스와 캔버스의 좌표를 같게 한다.\n"
            + "3. 기준 사본. `put_pixels`로 레퍼런스 전체를 Reference 레이어에 넣어 선택할 때 쓴다. `update_layer`로 visible false로 숨기고 마지막에 지운다.\n"
            + "4. 재료. 요소마다 선택한다 — Reference 레이어에서 `select` magic_wand, `select` lasso, 또는 `add_path` as=selection — 그리고 `put_pixels`에 within_selection true를 주어 그 요소의 레이어에 넣는다. 색은 원본 그대로다. 바탕 재료는 보이는 부분만이 아니라 모양 뒤에 있는 것까지 가져온다.\n"
            + "5. 가려진 부분. 텍스트나 다른 요소가 재료를 가리던 곳은 채운다. 빈 곳을 `select`하고 `fill_selection` content_aware true, 작은 곳은 `paint_stroke` tool heal이나 clone, 또는 주변 색으로 `paint_stroke` tool brush.\n"
            + "6. 모양. 잘라낸 부분과 실루엣은 마스크로 만들고 흰색으로 칠하지 않는다. 재료 레이어에 `add_path` as=mask(구멍은 fill_rule evenodd, 틈은 mode subtract), 또는 `select` 후 `set_mask` shape selection. 레퍼런스가 부드러운 곳은 feather를 준다.\n"
            + "7. 효과. 휘어짐, 소용돌이, 원근은 `liquify`·`warp_layer`·`distort_layer`, 색조는 `add_adjustment`, 그림자와 윤곽선은 `set_effects`로 만든다. 효과를 픽셀 단위로 따라 그리지 않는다.\n"
            + "8. 텍스트. 모든 글자를 `add_text`로 만들고 글꼴(`list_fonts`), 크기, 자간, 행간, 색, 위치를 맞춘다. 일부 글자만 다른 스타일은 `edit_text`의 start와 end로. 레퍼런스의 글자를 픽셀로 남기지 않는다.\n"
            + "9. 확인. `compare_image`로 레퍼런스와 비교해 점수와 가장 다른 영역을 읽고, 그 영역을 `render` zoom으로 보고 고친 뒤 다시 비교한다. Reference 레이어를 지운다. 마지막 점수와 아직 다른 부분을 보고한다."),
        (TextKey.AiTopicDesign,
            "# Making something new\n"
            + "\n"
            + "1. `new_document` at the final size. Plan groups by role: Background, Images, Shapes & decoration, Text, Adjustments.\n"
            + "2. Background: `add_gradient`, `add_shape`, or a picture with `add_image`.\n"
            + "3. Pictures: `add_image` for files you have, `generate_image` for photographs you cannot draw; place them with `update_layer`; cut them out with `remove_background`, `set_mask` or `add_path` as=mask.\n"
            + "4. Shapes: `add_shape` for regular ones, `add_path` for free ones; colour them with an `add_gradient` clipped to them (`set_clipping`).\n"
            + "5. Text: `add_text`, with hierarchy by size and weight; `set_effects` for shadows and outlines.\n"
            + "6. Finish: an `add_adjustment` over everything for one tone. `render` the whole and zoomed regions, and fix what looks off.",
            "# 새로 디자인\n"
            + "\n"
            + "1. `new_document`를 최종 크기로 만든다. 역할별 그룹을 계획한다: 배경, 이미지, 도형·장식, 텍스트, 보정.\n"
            + "2. 배경: `add_gradient`, `add_shape`, 또는 `add_image`로 사진.\n"
            + "3. 사진: 가진 파일은 `add_image`, 그릴 수 없는 사진은 `generate_image`. `update_layer`로 배치하고 `remove_background`, `set_mask`, `add_path` as=mask로 잘라낸다.\n"
            + "4. 도형: 규칙적인 것은 `add_shape`, 자유로운 것은 `add_path`. 색은 도형에 클리핑한 `add_gradient`로 칠한다(`set_clipping`).\n"
            + "5. 텍스트: `add_text`, 크기와 굵기로 위계를 준다. 그림자와 윤곽선은 `set_effects`.\n"
            + "6. 마무리: 전체 위에 `add_adjustment`로 색조를 통일한다. 전체와 확대 영역을 `render`로 보고 어색한 곳을 고친다."),
        (TextKey.AiTopicRetouch,
            "# Correcting a photo\n"
            + "\n"
            + "1. Keep the original: `duplicate_layer` the photo and work on the copy, or on new layers above it.\n"
            + "2. Blemishes and small objects: `paint_stroke` tool heal over them. Larger objects: `select` them, then `fill_selection` content_aware true. Repeated patterns: `paint_stroke` tool clone with a source.\n"
            + "3. Shape: `liquify` with small sizes and low pressure, several light passes (forward to push, pucker and bloat to shrink and swell); `warp_layer` and `distort_layer` for the whole layer.\n"
            + "4. Tone and colour: `add_adjustment` (levels and curves per channel, hue_saturation per colour range), kept to an area with `set_mask` or `paint_stroke` mask true.\n"
            + "5. Compare before and after with `render` zoomed on the regions changed, hiding the new layers to see before.",
            "# 사진 보정\n"
            + "\n"
            + "1. 원본을 지킨다. `duplicate_layer`로 사진을 복사해 사본에서, 또는 위에 새 레이어를 두고 작업한다.\n"
            + "2. 잡티와 작은 물체: `paint_stroke` tool heal로 덮는다. 큰 물체: `select`로 고른 뒤 `fill_selection` content_aware true. 반복 무늬: `paint_stroke` tool clone에 source를 준다.\n"
            + "3. 형태: `liquify`를 작은 크기와 낮은 압력으로 여러 번 가볍게(forward는 밀기, pucker와 bloat는 줄이기와 부풀리기). 레이어 전체는 `warp_layer`와 `distort_layer`.\n"
            + "4. 색조와 색: `add_adjustment`(레벨·곡선은 채널별, hue_saturation은 색 범위별). 일부에만 적용할 때는 `set_mask`나 `paint_stroke` mask true.\n"
            + "5. 바뀐 영역을 `render` zoom으로 보며 전후를 비교한다. 새 레이어를 숨기면 전 상태가 보인다."),
        (TextKey.UpdateAvailable, "Version {0} is available (you have {1}). Open the download page?", "새 버전 {0}이(가) 나왔습니다(현재 {1}). 다운로드 페이지를 열까요?"),
        (TextKey.UpdateLatest, "You have the latest version ({0}).", "최신 버전입니다({0})."),
        (TextKey.UpdateFailed, "Could not check for updates: {0}", "업데이트를 확인하지 못했습니다: {0}"),
        (TextKey.UpdateUnreadable, "the update feed could not be read", "업데이트 정보를 읽을 수 없습니다"),
        (TextKey.ToolCrop, "Crop Tool", "자르기 도구"),
        (TextKey.LabelAspectRatio, "Ratio", "가로세로 비율"),
        (TextKey.CropRatioFree, "Free", "자유"),
        (TextKey.CropRatioOriginal, "Original", "원본 비율"),
        (TextKey.NoteFrameSize, "{0} × {1} px", "{0} × {1}픽셀"),
        (TextKey.ButtonApplyCrop, "Apply Crop", "자르기 적용"),
        (TextKey.CommandTransformSelection, "&Transform Selection", "선택 영역 변형(&T)"),
        (TextKey.CommandTransformLayer, "&Transform Layer", "레이어 변형(&T)"),
        (TextKey.LayerFloatingSelection, "Floating Selection", "떠 있는 선택 영역"),
        (TextKey.NoteFloating, "Enter applies, Esc cancels", "Enter로 적용, Esc로 취소"),
        (TextKey.CommandPixelGrid, "&Pixel Grid (800% and above)", "픽셀 격자(800% 이상)(&P)"),
        (TextKey.CommandTransformControls, "Show &Transform Controls", "변형 컨트롤 표시(&T)"),
        (TextKey.CommandEditAdjustment, "&Edit Adjustment", "조정 편집(&E)"),
        (TextKey.HistoryArrangeLayers, "Arrange Layers", "레이어 순서 변경"),
        (TextKey.CommandAddHideMask, "Add &Hide-All Mask", "모두 가리는 마스크 추가(&H)"),
        (TextKey.CommandRenameLayer, "Rename…", "이름 바꾸기…"),
        (TextKey.ToolEyedropper, "Eyedropper Tool", "스포이드 도구"),
        (TextKey.ToolHand, "Hand Tool", "손 도구"),
        (TextKey.ToolZoom, "Zoom Tool", "돋보기 도구"),
        (TextKey.LabelSelectionMode, "Selection", "선택"),
        (TextKey.SelectionReplace, "New", "새 선택"),
        (TextKey.SelectionAdd, "Add", "추가"),
        (TextKey.SelectionSubtract, "Subtract", "빼기"),
        (TextKey.LabelSampleSize, "Sample Size", "표본 크기"),
        (TextKey.SamplePoint, "Point", "포인트"),
        (TextKey.Sample3By3, "3 × 3", "3 × 3"),
        (TextKey.Sample5By5, "5 × 5", "5 × 5"),
        (TextKey.LabelAllLayers, "All Layers", "모든 레이어"),
        (TextKey.LabelAutoSelect, "Auto Select", "자동 선택"),
        (TextKey.ToolIdle, "Select a tool", "도구를 선택하세요"),
        (TextKey.LabelSampleRing, "Sample Ring", "샘플 링"),
        (TextKey.ToolText, "Type Tool", "문자 도구"),
        (TextKey.LabelFont, "Font", "글꼴"),
        (TextKey.LabelBold, "Bold", "굵게"),
        (TextKey.LabelItalic, "Italic", "기울임"),
        (TextKey.AlignLeft, "Left", "왼쪽"),
        (TextKey.AlignCenter, "Center", "가운데"),
        (TextKey.AlignRight, "Right", "오른쪽"),
        (TextKey.LabelTracking, "Tracking", "자간"),
        (TextKey.LabelLeading, "Leading", "행간"),
        (TextKey.ButtonWarp, "Warp…", "뒤틀기…"),
        (TextKey.TitleWarpText, "Warp Text", "텍스트 뒤틀기"),
        (TextKey.LabelStyle, "Style", "스타일"),
        (TextKey.LabelBend, "Bend", "구부리기"),
        (TextKey.LabelHorizontalDistortion, "Horizontal Distortion", "가로 왜곡"),
        (TextKey.LabelVerticalDistortion, "Vertical Distortion", "세로 왜곡"),
        (TextKey.WarpNone, "None", "없음"),
        (TextKey.WarpArc, "Arc", "부채꼴"),
        (TextKey.WarpArcLower, "Arc Lower", "아래 부채꼴"),
        (TextKey.WarpArcUpper, "Arc Upper", "위 부채꼴"),
        (TextKey.WarpArch, "Arch", "아치"),
        (TextKey.WarpBulge, "Bulge", "돌출"),
        (TextKey.WarpShellLower, "Shell Lower", "아래가 넓은 조개"),
        (TextKey.WarpShellUpper, "Shell Upper", "위가 넓은 조개"),
        (TextKey.WarpFlag, "Flag", "깃발"),
        (TextKey.WarpWave, "Wave", "파형"),
        (TextKey.WarpFish, "Fish", "물고기"),
        (TextKey.WarpRise, "Rise", "상승"),
        (TextKey.WarpFisheye, "Fisheye", "물고기 눈"),
        (TextKey.WarpInflate, "Inflate", "부풀리기"),
        (TextKey.WarpSqueeze, "Squeeze", "양쪽 누르기"),
        (TextKey.WarpTwist, "Twist", "비틀기"),
        (TextKey.NoteTypeHint, "Click the canvas to add text, or click text to edit it", "캔버스를 클릭하면 글자를 넣고, 글자를 클릭하면 고칩니다"),
        (TextKey.ShapePolygon, "Polygon", "다각형"),
        (TextKey.ShapeStar, "Star", "별"),
        (TextKey.ShapeLine, "Line", "선"),
        (TextKey.LabelSides, "Sides", "변"),
        (TextKey.LabelPoints, "Points", "꼭짓점"),
        (TextKey.LabelInset, "Inset", "안쪽 깊이"),
        (TextKey.LabelCurved, "Curved", "곡선"),
        (TextKey.LabelFill, "Fill", "칠"),
        (TextKey.LabelStroke, "Stroke", "획"),
        (TextKey.LabelLineWidth, "Weight", "두께"),
        (TextKey.HistoryAddText, "Add Text", "텍스트 추가"),
        (TextKey.HistoryEditText, "Edit Text", "텍스트 편집"),
        (TextKey.HistoryTextStyle, "Text Style", "텍스트 스타일"),
        (TextKey.CommandRasterizeType, "Rasteri&ze Type", "문자 래스터화(&Z)"),
        (TextKey.CommandLayerStyle, "Layer St&yle", "레이어 스타일(&Y)"),
        (TextKey.HistoryLayerStyle, "Layer Style", "레이어 스타일"),
        (TextKey.EffectShadow, "Drop Shadow", "드롭 섀도"),
        (TextKey.EffectGlow, "Outer Glow", "외부 광선"),
        (TextKey.EffectStroke, "Stroke", "획"),
        (TextKey.LabelSpread, "Spread", "스프레드"),
        (TextKey.LabelPosition, "Position", "위치"),
        (TextKey.StrokeOutside, "Outside", "바깥쪽"),
        (TextKey.StrokeInside, "Inside", "안쪽"),
        (TextKey.StrokeCenter, "Center", "가운데"),
        (TextKey.LabelColour, "Color", "색상"),
        (TextKey.FileTypePsd, "PSD document", "PSD 문서"),
        (TextKey.FileTypeSupported, "All supported files", "지원하는 모든 파일"),
        (TextKey.CommandExportPsd, "Export as PS&D…", "PSD로 내보내기(&D)…"),
        (TextKey.ErrorPsdUnreadable, "It is not a PSD this program can read, or it is damaged.", "이 프로그램이 읽을 수 있는 PSD 파일이 아니거나 파일이 손상되었습니다."),
        (TextKey.PsdNotesOpened, "{0} is open, but some of it could not come across exactly:", "{0}을(를) 열었지만 일부는 그대로 옮기지 못했습니다."),
        (TextKey.PsdNotesSaved, "{0} is saved, but some of it could not go across exactly:", "{0}에 저장했지만 일부는 그대로 옮기지 못했습니다."),
        (TextKey.PsdNoteBlendModeReplaced, "Blend modes this program does not have, drawn as Normal: {0}", "이 프로그램에 없는 혼합 모드(표준으로 표시): {0}개"),
        (TextKey.PsdNoteAdjustmentDropped, "Adjustment layers of a kind this program does not have, left out: {0}", "이 프로그램에 없는 종류의 조정 레이어(빠짐): {0}개"),
        (TextKey.PsdNoteGradientMapSimplified, "Gradient maps kept as their two end colours: {0}", "양 끝 두 색으로 줄인 그레이디언트 맵: {0}개"),
        (TextKey.PsdNoteEffectDropped, "Layer effects this program does not have (bevel, inner shadow, overlays…), left out: {0}", "이 프로그램에 없는 레이어 효과(경사와 엠보스, 내부 그림자, 오버레이 등, 빠짐): {0}개"),
        (TextKey.PsdNoteEffectSimplified, "Strokes drawn in a single colour: {0}", "단색으로 바꾼 획 효과: {0}개"),
        (TextKey.PsdNoteTypeRasterized, "Type layers opened as pixels, so the words cannot be edited: {0}", "픽셀로 연 문자 레이어(글자는 고칠 수 없음): {0}개"),
        (TextKey.PsdNoteSmartObjectRasterized, "Smart objects opened as pixels: {0}", "픽셀로 연 고급 개체: {0}개"),
        (TextKey.PsdNoteVectorMaskDropped, "Vector masks left out: {0}", "빠진 벡터 마스크: {0}개"),
        (TextKey.PsdNoteMaskParametersDropped, "Mask density or feather not applied: {0}", "적용하지 않은 마스크 농도·페더: {0}개"),
        (TextKey.PsdNoteGroupMerged, "Groups with their own opacity or blend mode, merged into one layer: {0}", "자체 불투명도나 혼합 모드가 있어 한 레이어로 합친 그룹: {0}개"),
        (TextKey.PsdNoteClippingDropped, "Clipping masks that could not be kept: {0}", "유지하지 못한 클리핑 마스크: {0}개"),
        (TextKey.PsdNoteFlattenedOnly, "The file has no layers, so it opened as a single image.", "레이어가 없는 파일이라 한 장의 이미지로 열었습니다."),
        (TextKey.PsdNoteTextExportedAsPixels, "Text layers saved as pixels: {0}", "픽셀로 저장한 문자 레이어: {0}개"),
        (TextKey.PsdNoteGrainDropped, "Grain adjustments, which a PSD has no layer for, left out: {0}", "PSD에 해당 레이어가 없어 뺀 그레인 조정: {0}개"),
        (TextKey.PsdNoteClippingBaked, "Clipping saved into the layer pixels: {0}", "레이어 픽셀에 적용해 저장한 클리핑: {0}개"),
        (TextKey.PsdNoteAdjustmentSimplified, "Adjustment options a PSD cannot hold, left out: {0}", "PSD에 담을 수 없어 뺀 조정 옵션: {0}개"),

        (TextKey.MenuDistort, "&Distort", "왜곡(&D)"),
        (TextKey.FilterPinch, "Pinch", "핀치"),
        (TextKey.FilterSpherize, "Spherize", "구형화"),
        (TextKey.FilterTwirl, "Twirl", "돌리기"),
        (TextKey.FilterWave, "Wave", "파형"),
        (TextKey.FilterPolarCoordinates, "Polar Coordinates", "극좌표"),
        (TextKey.LabelWavelength, "Wavelength", "파장"),
        (TextKey.LabelAmplitude, "Amplitude", "진폭"),
        (TextKey.PolarFromRectangular, "Rectangular to Polar", "직교 좌표를 극좌표로"),
        (TextKey.PolarToRectangular, "Polar to Rectangular", "극좌표를 직교 좌표로"),

        (TextKey.MenuTransform, "Tr&ansform", "변형(&A)"),
        (TextKey.CommandTransformScale, "&Scale", "비율(&S)"),
        (TextKey.CommandTransformRotate, "&Rotate", "회전(&R)"),
        (TextKey.CommandTransformSkew, "S&kew", "기울이기(&K)"),
        (TextKey.CommandTransformDistort, "&Distort", "왜곡(&D)"),
        (TextKey.CommandTransformPerspective, "&Perspective", "원근(&P)"),
        (TextKey.CommandTransformWarp, "&Warp", "뒤틀기(&W)"),
        (TextKey.CommandRotate180, "Rotate 180°", "180° 회전"),
        (TextKey.CommandRotateClockwise, "Rotate 90° Clockwise", "90° 시계 방향 회전"),
        (TextKey.CommandRotateCounterclockwise, "Rotate 90° Counterclockwise", "90° 시계 반대 방향 회전"),
        (TextKey.HistoryWarp, "Warp", "뒤틀기"),
        (TextKey.CommandLiquify, "Liquify", "픽셀 유동화"),
        (TextKey.HistoryLiquify, "Liquify", "픽셀 유동화"),
        (TextKey.LiquifyForward, "Forward Warp", "뒤틀기"),
        (TextKey.LiquifyReconstruct, "Reconstruct", "재구성"),
        (TextKey.LiquifyTwirl, "Twirl", "돌리기"),
        (TextKey.LiquifyPucker, "Pucker", "오목"),
        (TextKey.LiquifyBloat, "Bloat", "볼록"),
        (TextKey.LiquifyPushLeft, "Push Left", "왼쪽 밀기"),
        (TextKey.LiquifyFreeze, "Freeze", "고정"),
        (TextKey.LiquifyThaw, "Thaw", "고정 해제"),
        (TextKey.LabelPressure, "Pressure", "압력"),
        (TextKey.ButtonRestoreAll, "Restore All", "모두 복구"),
        (TextKey.WarpCustom, "Custom", "사용자 정의"),
        (TextKey.LabelTransformMode, "Transform", "변형"),
        (TextKey.TransformModeFree, "Scale and Rotate", "크기·회전"),
        (TextKey.NoteWarpGrid, "Drag the grid's points or the picture itself. Enter applies, Esc cancels.", "격자의 점이나 그림을 끌어 모양을 바꿉니다. Enter로 적용, Esc로 취소합니다."),
        (TextKey.NoteLiquify, "Alt turns Twirl the other way.", "Alt를 누르면 반대로 돌립니다."),

        (TextKey.PsdNoteTypeSimplified, "Text layers opened in a single style (mixed styles or settings merged): {0}", "한 가지 서식으로 연 문자 레이어(글자별 서식이나 일부 설정을 합침): {0}개"),
        (TextKey.PsdNoteFontMissing, "Text layers in fonts that are not installed (shown as saved; edits use a substitute): {0}", "PC에 없는 글꼴을 쓰는 문자 레이어(저장된 모습 그대로 보이고, 고치면 대체 글꼴로 그림): {0}개"),
        (TextKey.PsdMissingFontNames, "Not installed: {0}", "설치되지 않은 글꼴: {0}"),
    ];

    /// <summary>Phrases that are the same in both languages on purpose — names, not words.</summary>
    public static readonly FrozenSet<TextKey> Untranslated =
        new[]
        {
            TextKey.LanguageEnglish, TextKey.LanguageKorean, TextKey.UnitPercent, TextKey.ChannelRgb,
            TextKey.LabelR, TextKey.LabelG, TextKey.LabelB, TextKey.Sample3By3, TextKey.Sample5By5,
        }.ToFrozenSet();

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
