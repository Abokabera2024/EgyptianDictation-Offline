using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using EgyptianDictation.Contracts;

namespace EgyptianDictation.ArabicSTTWorker;

internal sealed class NativeCohereEngine : IWorkerEngine
{
    private readonly string _artifactDirectory;
    private readonly string _requestedBackend;
    private IntPtr _model;
    private IntPtr _session;
    private NativeMethods.AbortCallback? _abortCallback;

    public bool IsReady => _session != IntPtr.Zero;
    public string Backend { get; private set; } = "cpu";
    public int Threads { get; private set; }
    public double ModelLoadSeconds { get; private set; }

    public NativeCohereEngine(string? artifactDirectory = null, string requestedBackend = "cpu")
    {
        _artifactDirectory = string.IsNullOrWhiteSpace(artifactDirectory) ? AppContext.BaseDirectory : artifactDirectory;
        _requestedBackend = requestedBackend;
    }

    public void Load(string modelPath, int threads)
    {
        if (IsReady)
            return;
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("نموذج الكلام العربي دون إنترنت غير مثبت.", modelPath);
        var model = new FileInfo(modelPath);
        if (!model.Extension.Equals(".gguf", StringComparison.OrdinalIgnoreCase) || model.Length != OfflineModel.FileSize)
            throw new InvalidDataException("ملف نموذج Cohere غير صالح أو غير مكتمل.");
        using (var stream = model.OpenRead())
        {
            var hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!hash.Equals(OfflineModel.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("بصمة نموذج Cohere لا تطابق الإصدار Q5_K_M المعتمد.");
        }

        LogStage($"init_backends artifact={_artifactDirectory}");
        Check(NativeMethods.transcribe_init_backends(_artifactDirectory), "تهيئة CPU");
        var load = new NativeMethods.ModelLoadParams();
        NativeMethods.transcribe_model_load_params_init(ref load);
        load.Backend = _requestedBackend == "auto" &&
            File.Exists(Path.Combine(_artifactDirectory, "ggml-vulkan.dll")) &&
            NativeMethods.transcribe_backend_available(3) ? 3 : 1;
        load.GpuDevice = 0;
        var started = Stopwatch.StartNew();
        LogStage("model_load");
        Check(NativeMethods.transcribe_model_load_file(model.FullName, ref load, out _model), "تحميل النموذج");
        Threads = threads > 0 ? threads : Math.Clamp(Environment.ProcessorCount - 2, 1, 8);
        var session = new NativeMethods.SessionParams();
        NativeMethods.transcribe_session_params_init(ref session);
        session.Threads = Threads;
        LogStage($"session_init threads={Threads}");
        Check(NativeMethods.transcribe_session_init(_model, ref session, out _session), "إنشاء جلسة الاستدلال");
        started.Stop();
        ModelLoadSeconds = started.Elapsed.TotalSeconds;
        Backend = Utf8(NativeMethods.transcribe_model_backend(_model));
    }

    public WorkerTranscription Transcribe(string audioPath, string language, CancellationToken cancellationToken)
    {
        if (!IsReady)
            throw new InvalidOperationException("Arabic speech model is not loaded.");
        var (samples, seconds) = WavePcmReader.Read16KhzMono(audioPath);
        if (samples.Length == 0)
            throw new InvalidDataException("ملف الصوت فارغ.");

        var abortState = new AbortState();
        using var registration = cancellationToken.Register(() => abortState.Cancelled = true);
        var handle = GCHandle.Alloc(abortState);
        _abortCallback = data => ((AbortState)GCHandle.FromIntPtr(data).Target!).Cancelled;
        NativeMethods.transcribe_set_abort_callback(_session, _abortCallback, GCHandle.ToIntPtr(handle));
        var languagePointer = Marshal.StringToCoTaskMemUTF8(string.IsNullOrWhiteSpace(language) ? "ar" : language);
        try
        {
            var parameters = new NativeMethods.RunParams();
            NativeMethods.transcribe_run_params_init(ref parameters);
            parameters.Language = languagePointer;
            var started = Stopwatch.StartNew();
            var status = NativeMethods.transcribe_run(_session, samples, samples.Length, ref parameters);
            started.Stop();
            if (cancellationToken.IsCancellationRequested || NativeMethods.transcribe_was_aborted(_session))
                throw new OperationCanceledException(cancellationToken);
            Check(status, "تحويل الصوت إلى نص");
            return new WorkerTranscription(Utf8(NativeMethods.transcribe_full_text(_session)).Trim(),
                seconds, started.Elapsed.TotalSeconds);
        }
        finally
        {
            NativeMethods.transcribe_set_abort_callback(_session, null, IntPtr.Zero);
            _abortCallback = null;
            handle.Free();
            Marshal.FreeCoTaskMem(languagePointer);
        }
    }

    public void Dispose()
    {
        if (_session != IntPtr.Zero)
        {
            NativeMethods.transcribe_session_free(_session);
            _session = IntPtr.Zero;
        }
        if (_model != IntPtr.Zero)
        {
            NativeMethods.transcribe_model_free(_model);
            _model = IntPtr.Zero;
        }
    }

    private static void Check(int status, string operation)
    {
        if (status == 0)
            return;
        throw new InvalidOperationException($"فشل {operation}: {Utf8(NativeMethods.transcribe_status_string(status))}");
    }

    private static void LogStage(string stage)
    {
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EgyptianDictation", "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "ArabicSTTWorker.log"),
                $"{DateTime.UtcNow:o} stage={stage}{Environment.NewLine}");
        }
        catch { }
    }

    private static string Utf8(IntPtr pointer) => pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUTF8(pointer) ?? string.Empty;

    private sealed class AbortState { public volatile bool Cancelled; }

    private static class NativeMethods
    {
        private const string Library = "transcribe.dll";

        [StructLayout(LayoutKind.Sequential)]
        internal struct ModelLoadParams { public ulong StructSize; public int Backend; public int GpuDevice; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SessionParams { public ulong StructSize; public int Threads; public int KvType; public int Context; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RunParams
        {
            public ulong StructSize;
            public int Task;
            public int Timestamps;
            public int Pnc;
            public int Itn;
            public IntPtr Language;
            public IntPtr TargetLanguage;
            [MarshalAs(UnmanagedType.I1)] public bool KeepSpecialTags;
            public IntPtr Family;
            public int SpecDrafts;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal delegate bool AbortCallback(IntPtr userData);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int transcribe_init_backends([MarshalAs(UnmanagedType.LPUTF8Str)] string artifactDirectory);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool transcribe_backend_available(int kind);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void transcribe_model_load_params_init(ref ModelLoadParams parameters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void transcribe_session_params_init(ref SessionParams parameters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void transcribe_run_params_init(ref RunParams parameters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int transcribe_model_load_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref ModelLoadParams parameters, out IntPtr model);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int transcribe_session_init(IntPtr model, ref SessionParams parameters, out IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int transcribe_run(IntPtr session, [In] float[] pcm, int sampleCount, ref RunParams parameters);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr transcribe_full_text(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr transcribe_status_string(int status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr transcribe_model_backend(IntPtr model);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void transcribe_set_abort_callback(IntPtr session, AbortCallback? callback, IntPtr userData);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool transcribe_was_aborted(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void transcribe_session_free(IntPtr session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void transcribe_model_free(IntPtr model);
    }
}
