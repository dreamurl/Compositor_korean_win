using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// Filter › Remove Background: the model finds the subject, and the background goes behind a
/// layer mask — upstream's <c>removeBackground</c> filter.
/// </summary>
/// <remarks>
/// <para>
/// The model is the slow part — seconds on a CPU, and the first use loads it too — so it runs on a
/// background thread while the sheet says so, and wakes the window when it is done. It runs once;
/// every setting on the sheet is worked on its answer afterwards (<see cref="BackgroundRemoval"/>).
/// </para>
/// <para>
/// The preview is the document with the layer's mask swapped for one refined at preview size, which
/// the compositor already knows how to draw. Cancel puts the document back; OK refines at full size
/// and lays that mask down as one history step, then makes the mask the target, as upstream does.
/// </para>
/// </remarks>
internal sealed partial class CanvasView
{
    private sealed class BackgroundJob
    {
        public required ImageLayer Layer { get; init; }
        public required DocumentSelection? Selection { get; init; }
        public required CanvasDocument Before { get; init; }
        public required Task<(float[]? Logits, int Side, string? Error)> Work { get; init; }
        public float[]? Logits { get; set; }
        public int Side { get; set; }
        public string? Error { get; set; }
        public bool Done { get; set; }
        public PixelBuffer? Shown { get; set; }
    }

    private BackgroundJob? _background;

    /// <summary>Asks the window to draw again, from any thread — how a finished model run is seen.</summary>
    public Action? Wake { get; set; }

    /// <summary>Whether this build can remove backgrounds at all.</summary>
    public static bool CanRemoveBackgrounds => SubjectModels.Installed;

    /// <summary>The model is still working.</summary>
    public bool BackgroundWorking => _background is { Done: false };

    /// <summary>The model has answered and found something: OK has something to keep.</summary>
    public bool BackgroundReady => _background is { Done: true, Error: null, Logits: not null };

    /// <summary>Why there is nothing to keep, once the model has given up, or null.</summary>
    public string? BackgroundFailure => _background is { Done: true, Error: string error } ? error : null;

    private void StartBackground(ImageLayer layer)
    {
        if (_document is null || layer.Image is not PixelBuffer pixels) return;

        PixelBuffer image = pixels.Retain();
        Action? wake = Wake;
        var work = Task.Run<(float[]? Logits, int Side, string? Error)>(() =>
        {
            try
            {
                if (!SubjectModels.Installed) return (null, 0, Localizer.Text(TextKey.NoteAiUnavailable));
                if (SubjectModels.Shared(out string? error) is not ISubjectModel model)
                    return (null, 0, Localizer.Format(TextKey.NoteAiFailed, error ?? string.Empty));

                float[] logits = model.Predict(SubjectMatte.Input(image, model.Side));
                return BackgroundRemoval.FoundSubject(logits)
                    ? (logits, model.Side, null)
                    : (null, 0, Localizer.Text(TextKey.NoteNoSubject));
            }
            catch (Exception exception)
            {
                return (null, 0, Localizer.Format(TextKey.NoteAiFailed, exception.Message));
            }
            finally
            {
                image.Release();
                wake?.Invoke();
            }
        });

        _background = new BackgroundJob { Layer = layer, Selection = _selection, Before = _document, Work = work };
    }

    /// <summary>Picks up a model run that has finished. The window calls it before every frame.</summary>
    public void Tick()
    {
        if (_background is not { Done: false } job || !job.Work.IsCompleted) return;

        (job.Logits, job.Side, job.Error) = job.Work.Result;
        job.Done = true;
        ShowBackground();
    }

    /// <summary>The document as it would be with the background removed, or as it was with preview off.</summary>
    private void ShowBackground()
    {
        if (_background is not BackgroundJob job) return;

        PixelBuffer? mask = null;
        if (job.Logits is float[] logits && _previewOn)
        {
            mask = BackgroundRemoval.Mask(job.Layer, logits, job.Side, _filterSettings.Background, job.Selection,
                                          BackgroundRemoval.PreviewLimit, fullSize: false);
        }

        _document = mask is null ? job.Before : job.Before.Replacing(BackgroundRemoval.WithMask(job.Layer, mask));
        job.Shown?.Release();
        job.Shown = mask;
        NeedsRedraw = true;
    }

    /// <summary>OK lays the full mask down as one step; Cancel, or nothing found, leaves the document as it was.</summary>
    private void FinishBackground(bool keep)
    {
        if (_background is not BackgroundJob job) return;
        _background = null;

        _document = job.Before;
        if (keep && job.Logits is float[] logits)
        {
            _lastFilterSettings[_command] = _filterSettings;
            PixelBuffer mask = BackgroundRemoval.Mask(job.Layer, logits, job.Side, _filterSettings.Background,
                                                      job.Selection, BackgroundRemoval.CommitLimit);
            _history.Begin(FilterTitle(_command), _document, job.Layer.Id);
            _document = _document.Replacing(BackgroundRemoval.WithMask(job.Layer, mask));
            _history.End(_document, job.Layer.Id);
            _maskOf = job.Layer.Id;
        }

        job.Shown?.Release();
        job.Shown = null;
        NeedsRedraw = true;
    }
}
