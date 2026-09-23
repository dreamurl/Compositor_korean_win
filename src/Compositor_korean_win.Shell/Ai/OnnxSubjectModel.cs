using Compositor_korean_win.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Compositor_korean_win.Shell;

/// <summary>
/// BiRefNet-lite through ONNX Runtime: on the GPU through DirectML when there is one that will
/// take it, on the CPU otherwise.
/// </summary>
/// <remarks>
/// <para>
/// The runtime and the model ship only in the AI build, beside the exe
/// (<c>onnxruntime.dll</c>, <c>DirectML.dll</c>, <c>models\birefnet-lite-fp16.onnx</c>). The plain
/// build has none of them, and nothing here is touched until background removal is asked for, so
/// the plain build never tries to load a DLL it does not have: <see cref="Availability"/> looks for
/// the files before anything binds to them.
/// </para>
/// <para>
/// DirectML is vendor-neutral, so NVIDIA, AMD and Intel all run it. It is tried first; a device
/// that cannot build the session — no D3D12, a driver it will not use — falls back to the CPU,
/// which is slower but gives the same answer.
/// </para>
/// </remarks>
internal sealed class OnnxSubjectModel : ISubjectModel, IDisposable
{
    public const string ModelFile = "birefnet-lite-fp16.onnx";

    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string _output;

    private OnnxSubjectModel(InferenceSession session, string device)
    {
        _session = session;
        Device = device;
        _input = session.InputMetadata.Keys.First();
        _output = session.OutputMetadata.Keys.First();
        Side = session.InputMetadata[_input].Dimensions is [_, _, int height, _] && height > 0 ? height : 1024;
    }

    public int Side { get; }

    public string Device { get; }

    /// <summary>Where the model is expected: <c>models</c> beside the exe.</summary>
    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "models", ModelFile);

    /// <summary>Whether this is the AI build: the runtime and the model are both beside the exe.</summary>
    public static bool Availability =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll")) && File.Exists(ModelPath);

    /// <summary>Why DirectML was passed over for the CPU, when it was — for the report.</summary>
    public static string? GpuError { get; private set; }

    /// <summary>
    /// The model loaded, or null with the reason. Loading is the slow part — a second or more — so
    /// the caller keeps the result.
    /// </summary>
    public static OnnxSubjectModel? Load(out string? error)
    {
        error = null;
        if (!Availability)
        {
            error = "not installed";
            return null;
        }

        try
        {
            try
            {
                // DirectML wants memory patterns off and one node at a time; it schedules itself.
                using var gpu = new SessionOptions { EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL };
                gpu.AppendExecutionProvider_DML(0);
                return new OnnxSubjectModel(new InferenceSession(ModelPath, gpu), "DirectML");
            }
            catch (OnnxRuntimeException exception)
            {
                GpuError = exception.Message;
                using var cpu = new SessionOptions();
                return new OnnxSubjectModel(new InferenceSession(ModelPath, cpu), "CPU");
            }
        }
        catch (Exception exception) when (exception is OnnxRuntimeException or DllNotFoundException
                                              or TypeInitializationException or BadImageFormatException
                                              or EntryPointNotFoundException)
        {
            error = exception.Message;
            return null;
        }
    }

    /// <summary>One run at a time on this session.</summary>
    /// <remarks>
    /// The DirectML provider does not take two runs on one session at once: on an RTX 4060 the
    /// second of two overlapping runs failed in DmlCommandRecorder (80004005), while each alone ran
    /// in two seconds. Overlap is ordinary here — Cancel leaves a run going and reopening the sheet
    /// starts another — so the runs queue instead. The CPU provider would allow it, which is why the
    /// GPU-less CI never showed the failure.
    /// </remarks>
    private readonly Lock _running = new();

    public float[] Predict(float[] planes)
    {
        var tensor = new DenseTensor<float>(planes, [1, 3, Side, Side]);
        lock (_running)
        {
            using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results =
                _session.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)]);
            DisposableNamedOnnxValue result = results.First(value => value.Name == _output);
            return [.. result.AsEnumerable<float>()];
        }
    }

    public void Dispose() => _session.Dispose();
}
