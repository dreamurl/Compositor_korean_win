using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>The subject model for this build: the plain build has none.</summary>
/// <remarks>
/// Compiled only into the plain build, in place of <c>SubjectModels.Onnx.cs</c>. Remove Background
/// stays on the menu, greyed, saying which build has it.
/// </remarks>
internal static class SubjectModels
{
    public static bool Installed => false;

    public static string? GpuError => null;

    public static ISubjectModel? Shared(out string? error)
    {
        error = "not installed";
        return null;
    }
}
