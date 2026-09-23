using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>The subject model for this build: the AI build's, loaded once and shared.</summary>
/// <remarks>
/// Compiled only into the AI build. The plain build has <c>SubjectModels.None.cs</c> in its place,
/// with no ONNX Runtime referenced at all, so nothing in it can reach for a DLL it does not have.
/// </remarks>
internal static class SubjectModels
{
    private static readonly Lock s_gate = new();
    private static OnnxSubjectModel? s_model;
    private static string? s_error;

    /// <summary>Whether the runtime and the model are beside the exe.</summary>
    public static bool Installed => OnnxSubjectModel.Availability;

    /// <summary>Why the GPU was not used, when it was not.</summary>
    public static string? GpuError => OnnxSubjectModel.GpuError;

    /// <summary>
    /// The model, loaded on first use — seconds on a CPU — and kept for the session. Safe to call
    /// from a background thread, which is where it is called from.
    /// </summary>
    public static ISubjectModel? Shared(out string? error)
    {
        lock (s_gate)
        {
            if (s_model is null && s_error is null) s_model = OnnxSubjectModel.Load(out s_error);
            error = s_error;
            return s_model;
        }
    }
}
