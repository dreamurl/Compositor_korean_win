using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>
/// What a project has to satisfy before it is allowed anywhere near the open document.
/// </summary>
/// <remarks>
/// Every rule here exists upstream, and each one is the difference between rejecting a damaged file
/// and misrendering it. A file on disk is untrusted input: these run on load, and on save too,
/// because a manifest that cannot be read back is not worth writing.
/// </remarks>
public class ManifestValidationTests
{
    private static ProjectException Rejects(ProjectManifest manifest) =>
        Assert.Throws<ProjectException>(() => ManifestValidator.Validate(manifest));

    [Fact]
    public void APlainManifestIsAccepted() => ManifestValidator.Validate(ProjectFixture.Manifest());

    [Theory]
    [InlineData(0)]
    [InlineData(ProjectManifest.CurrentVersion + 1)]
    [InlineData(99)]
    public void AnUnknownVersionIsRejectedByVersion(int version)
    {
        ProjectException rejected = Rejects(ProjectFixture.Manifest() with { Version = version });

        // The kind matters: this is the one failure a user can act on, by updating the app.
        Assert.Equal(ProjectErrorKind.Version, rejected.Kind);
        Assert.Equal(version, rejected.Version);
    }

    [Fact]
    public void AForeignFormatIsRejected() =>
        Assert.Equal(ProjectErrorKind.Invalid,
            Rejects(ProjectFixture.Manifest() with { Format = "com.example.other" }).Kind);

    [Fact]
    public void AnotherColourSpaceIsRejected() =>
        Assert.Equal(ProjectErrorKind.Invalid,
            Rejects(ProjectFixture.Manifest() with { ColorSpace = "Display P3" }).Kind);

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(ProjectLimits.MaximumSide + 1)]
    public void AnImpossibleCanvasIsTooLarge(int side)
    {
        Assert.Equal(ProjectErrorKind.TooLarge, Rejects(ProjectFixture.Manifest() with { Width = side }).Kind);
        Assert.Equal(ProjectErrorKind.TooLarge, Rejects(ProjectFixture.Manifest() with { Height = side }).Kind);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(9601)]
    [InlineData(double.NaN)]
    public void AnOutOfRangeResolutionIsRejected(double resolution) =>
        Assert.Equal(ProjectErrorKind.Invalid,
            Rejects(ProjectFixture.Manifest() with { Resolution = resolution }).Kind);

    [Fact]
    public void TooManyLayersIsTooLarge()
    {
        IEnumerable<ProjectLayerRecord> layers =
            Enumerable.Range(0, ProjectLimits.MaximumLayers + 1)
                      .Select(i => ProjectFixture.Record($"Layer {i}"));

        Assert.Equal(ProjectErrorKind.TooLarge,
            Rejects(ProjectFixture.Manifest() with { Layers = new EquatableList<ProjectLayerRecord>(layers) }).Kind);
    }

    [Fact]
    public void ADuplicateLayerIdIsRejected()
    {
        Guid id = Guid.NewGuid();
        Rejects(ProjectFixture.Manifest(layers:
            [ProjectFixture.Record("One", id), ProjectFixture.Record("Two", id)]));
    }

    [Fact]
    public void AnActiveLayerThatIsNotThereIsRejected() =>
        Rejects(ProjectFixture.Manifest() with { ActiveLayerId = Guid.NewGuid() });

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t ")]
    public void ABlankLayerNameIsRejected(string name) =>
        Rejects(ProjectFixture.Manifest(layers: [ProjectFixture.Record("x") with { Name = name }]));

    [Fact]
    public void AnOverlongLayerNameIsRejected() =>
        Rejects(ProjectFixture.Manifest(layers:
            [ProjectFixture.Record("x") with { Name = new string('가', ProjectLimits.MaximumNameBytes) }]));

    [Fact]
    public void AnAssetNamedAfterAnotherLayerIsRejected()
    {
        // The filename is checked against the layer's own id, which is what stops a manifest from
        // pointing a layer at a file it does not own — or at a path outside the project.
        ProjectLayerRecord record = ProjectFixture.Record("Layer 1") with
        {
            ImageFile = ManifestValidator.ImageFileName(Guid.NewGuid()),
        };

        Rejects(ProjectFixture.Manifest(layers: [record]));
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("images/nested.png")]
    [InlineData("anything.png")]
    public void AnAssetPathThatIsNotTheLayersOwnIsRejected(string file) =>
        Rejects(ProjectFixture.Manifest(layers: [ProjectFixture.Record("Layer 1") with { ImageFile = file }]));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(1e7)]
    [InlineData(0)]
    public void AnOutOfRangeTransformIsRejected(double width) =>
        Rejects(ProjectFixture.Manifest(layers:
            [
                ProjectFixture.Record("Layer 1") with
                {
                    Transform = new LayerTransform(Point.Zero, new Size(width, 10)),
                },
            ]));

    // --- Version gates -----------------------------------------------------------------------

    [Fact]
    public void VersionOneCannotCarryFolders() =>
        Rejects(ProjectFixture.Manifest(version: 1,
            layers: [ProjectFixture.Record("Folder") with { IsGroup = true }]));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void VersionsBeforeThreeCannotCarryAppearance(int version)
    {
        Rejects(ProjectFixture.Manifest(version, [ProjectFixture.Record("Layer 1") with { Opacity = 0.5 }]));
        Rejects(ProjectFixture.Manifest(version,
            [ProjectFixture.Record("Layer 1") with { BlendMode = LayerBlendMode.Multiply }]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void VersionsBeforeFourCannotCarryLayerMasks(int version)
    {
        Guid id = Guid.NewGuid();
        Rejects(ProjectFixture.Manifest(version,
            [ProjectFixture.Record("Layer 1", id) with { MaskFile = ManifestValidator.MaskFileName(id) }]));
    }

    [Fact]
    public void VersionFourAcceptsALayerMask()
    {
        Guid id = Guid.NewGuid();
        ManifestValidator.Validate(ProjectFixture.Manifest(4,
            [ProjectFixture.Record("Layer 1", id) with { MaskFile = ManifestValidator.MaskFileName(id) }]));
    }

    [Fact]
    public void VersionFiveCannotGiveAFolderAMask()
    {
        // Folder masks arrived in version 6; a version-5 file claiming one is a file that some
        // other build would render differently.
        Guid id = Guid.NewGuid();
        Rejects(ProjectFixture.Manifest(5,
            [
                ProjectFixture.Record("Folder", id) with
                {
                    IsGroup = true,
                    MaskFile = ManifestValidator.MaskFileName(id),
                },
            ]));
    }

    [Fact]
    public void VersionSixAcceptsAFolderMask()
    {
        Guid id = Guid.NewGuid();
        ManifestValidator.Validate(ProjectFixture.Manifest(6,
            [
                ProjectFixture.Record("Folder", id) with
                {
                    IsGroup = true,
                    MaskFile = ManifestValidator.MaskFileName(id),
                },
            ]));
    }

    [Fact]
    public void VersionsBeforeFiveCannotCarryClippingMasks()
    {
        Guid baseId = Guid.NewGuid();
        Rejects(ProjectFixture.Manifest(4,
            [
                ProjectFixture.Record("Base", baseId),
                ProjectFixture.Record("Clipped") with { MaskSourceId = baseId },
            ]));
    }

    [Fact]
    public void VersionsBeforeSevenCannotCarryAdjustments() =>
        Rejects(ProjectFixture.Manifest(6,
            [ProjectFixture.Record("Levels") with { Adjustment = new LayerAdjustment(AdjustmentKind.Levels) }]));

    // --- Layer consistency -------------------------------------------------------------------

    [Fact]
    public void AMaskFlagWithoutAMaskIsRejected() =>
        Rejects(ProjectFixture.Manifest(layers: [ProjectFixture.Record("Layer 1") with { MaskEnabled = true }]));

    [Fact]
    public void AMaskPlacementWithoutAMaskIsRejected() =>
        Rejects(ProjectFixture.Manifest(layers:
            [
                ProjectFixture.Record("Layer 1") with
                {
                    MaskPlacement = new LayerTransform(Point.Zero, new Size(4, 4)),
                },
            ]));

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void AnOutOfRangeOpacityIsRejected(double opacity) =>
        Rejects(ProjectFixture.Manifest(layers: [ProjectFixture.Record("Layer 1") with { Opacity = opacity }]));

    [Fact]
    public void AFolderCannotCarryOpacityOrABlendMode()
    {
        // Folders are pass-through in every version this build reads; giving one an opacity would
        // silently mean something different here than on macOS.
        Rejects(ProjectFixture.Manifest(layers:
            [ProjectFixture.Record("Folder") with { IsGroup = true, Opacity = 0.5 }]));
        Rejects(ProjectFixture.Manifest(layers:
            [ProjectFixture.Record("Folder") with { IsGroup = true, BlendMode = LayerBlendMode.Screen }]));
    }

    [Fact]
    public void AFolderCannotBeAnAdjustment() =>
        Rejects(ProjectFixture.Manifest(layers:
            [
                ProjectFixture.Record("Folder") with
                {
                    IsGroup = true,
                    Adjustment = new LayerAdjustment(AdjustmentKind.Curves),
                },
            ]));

    [Fact]
    public void AnAdjustmentCannotCarryPixels()
    {
        Guid id = Guid.NewGuid();
        Rejects(ProjectFixture.Manifest(layers:
            [
                ProjectFixture.Record("Curves", id) with
                {
                    ImageFile = ManifestValidator.ImageFileName(id),
                    Adjustment = new LayerAdjustment(AdjustmentKind.Curves),
                },
            ]));
    }

    [Fact]
    public void AnAdjustmentWithImpossibleSettingsIsRejected()
    {
        var adjustment = new LayerAdjustment(AdjustmentKind.Exposure)
        {
            ExposureSettings = new ExposureSettings { Exposure = 40 },
        };

        Rejects(ProjectFixture.Manifest(layers:
            [ProjectFixture.Record("Exposure") with { Adjustment = adjustment }]));
    }

    [Fact]
    public void ACurveThatDoublesBackIsRejected()
    {
        var curves = new CurvesSettings
        {
            Channels = new EquatableList<EquatableList<CurvePoint>>(
            [
                new([new CurvePoint(0, 0), new CurvePoint(200, 100), new CurvePoint(100, 200), new CurvePoint(255, 255)]),
                new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
                new([new CurvePoint(0, 0), new CurvePoint(255, 255)]),
            ]),
        };

        Assert.False(curves.IsValid);
        Rejects(ProjectFixture.Manifest(layers:
            [
                ProjectFixture.Record("Curves") with
                {
                    Adjustment = new LayerAdjustment(AdjustmentKind.Curves) { Curves = curves },
                },
            ]));
    }

    [Fact]
    public void ALevelsRangeThatIsNotItsOwnNormalisedFormIsRejected()
    {
        // Upstream stores Levels already clamped and checks that on load, so a file with a white
        // point below its black point is a file someone edited by hand.
        var levels = new LevelsSettings
        {
            Ranges = new EquatableList<LevelRange>([new LevelRange { Black = 200, White = 10 }, new(), new(), new()]),
        };

        Rejects(ProjectFixture.Manifest(layers:
            [
                ProjectFixture.Record("Levels") with
                {
                    Adjustment = new LayerAdjustment(AdjustmentKind.Levels) { Levels = levels },
                },
            ]));
    }
}
