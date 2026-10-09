using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Speech-to-text engine abstraction.
//
// Engines consume 16 kHz mono 16-bit little-endian PCM — the same format the
// capture service produces (a PipeWire recorder at 16000 Hz, one channel).
// Transcription is CPU/GPU-heavy: callers must invoke engines from a dedicated
// background worker only, never from the audio capture thread or the UI
// thread. Engines are single-caller; they are not thread-safe.
public interface ISttEngine : IDisposable
{
    const int SampleRate = 16000;
    const int BytesPerSample = 2;

    // Short human-readable engine/model identifier for logs and UI.
    string Name { get; }

    bool IsLoaded { get; }

    // Loads the model into memory. Returns false with an error message when the
    // model is missing or fails to load; never throws for a missing model.
    bool TryLoad(out string? error);

    // Transcribes one complete utterance ("length" bytes of 16 kHz mono 16-bit
    // PCM from the start of "pcm") and returns clean text, "" when nothing was
    // recognized. Requires a successful TryLoad first.
    Task<string> TranscribeAsync(byte[] pcm, int length, CancellationToken ct = default);

    // Experimental name recognition: terms (player display names) the engine
    // should bias toward. Best-effort, applied at load time; engines that
    // can't bias simply ignore it. Never called unless the user enabled the
    // feature — the default path must carry zero overhead.
    void SetBiasTerms(System.Collections.Generic.IReadOnlyList<string> terms) { }

    // Transcription with the engine's own timing for its parts (Whisper:
    // segments; Parakeet: words) — what lets the pipeline trim audio under
    // committed words. Engines without timing return no spans.
    async Task<SttTranscript> TranscribeTimedAsync(byte[] pcm, int length, CancellationToken ct = default)
        => new SttTranscript(await TranscribeAsync(pcm, length, ct).ConfigureAwait(false), SttTranscript.NoSpans);

    // Longest audio one pass may be handed, in ms; 0 = no engine limit.
    // Whisper on the CPU runs a shortened encoder context (WhisperNetEngine)
    // and must never see more than that.
    int MaxWindowMs => 0;
}

// One timed part of a hypothesis, relative to the start of the audio the
// engine was given.
public readonly record struct SttSpan(string Text, int StartMs, int EndMs);

// A hypothesis plus its timed parts. When Spans is non-empty, Text is the
// join of the span texts, so word counts line up between the two.
public sealed record SttTranscript(string Text, System.Collections.Generic.IReadOnlyList<SttSpan> Spans)
{
    public static readonly System.Collections.Generic.IReadOnlyList<SttSpan> NoSpans = Array.Empty<SttSpan>();
}

// Shared locations for the app's files. User-local, so model weights can
// never end up committed to the repository.
public static class SttPaths
{
    // ~/.local/share/Chatterbox (XDG_DATA_HOME) unless the host points
    // elsewhere (--data-dir): settings, logs, the boot sentinel, the
    // unpacked UI and the models all live under it.
    public static string DataDir { get; set; } = DefaultDataDir();

    private static string DefaultDataDir() => OperatingSystem.IsLinux()
        ? Path.Combine(LinuxHost.DataHome, "Chatterbox")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Chatterbox");

    private static string? _modelDir;
    public static string ModelDir
    {
        get => _modelDir ?? Path.Combine(DataDir, "models", "stt");
        set => _modelDir = value;
    }

    // Where runtimes/ lives — the bundled whisper natives, the CUDA pack,
    // the Parakeet engine pack and the translation packs: the data folder,
    // always (see ChooseRuntimeRoot). Whisper.net probes <root>/runtimes/<rid>
    // (Program points it at this root).
    private static string? _runtimeRoot;
    public static string RuntimeRoot
    {
        get => _runtimeRoot ??= ChooseRuntimeRoot();
        set => _runtimeRoot = value;
    }

    public const string Rid = "linux-x64";
    public static string NativeDir => Path.Combine(RuntimeRoot, "runtimes", Rid);
    // The no-AVX whisper build: Whisper.net probes runtimes/noavx/<rid>
    // and loads it instead of the AVX2 build on a CPU without AVX2/FMA.
    public static string NoAvxNativeDir => Path.Combine(RuntimeRoot, "runtimes", "noavx", Rid);
    public static string CudaDir => Path.Combine(RuntimeRoot, "runtimes", "cuda", Rid);

    // Always the data folder: a pack downloaded before "Add to app grid"
    // would otherwise be stranded beside the old copy, and --purge could
    // not find it. (Program.MigrateRuntimes moves such leftovers here.)
    private static string ChooseRuntimeRoot() => DataDir;

    internal static bool IsWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".chatterbox-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}

// Shared PCM helpers for engines and the VAD.
public static class SttAudio
{
    // whisper.cpp context creation lazily initializes ggml process-global
    // state; two concurrent loads can race and corrupt it (observed:
    // GGML_ASSERT(wtype != GGML_TYPE_COUNT) crash). Every whisper.cpp
    // model/VAD load must hold this lock.
    public static readonly object NativeLoadLock = new();

    // Converts 16-bit little-endian PCM to normalized float samples.
    public static float[] ToFloat(byte[] pcm, int length)
    {
        var samples = new float[length / ISttEngine.BytesPerSample];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8));
            samples[i] = s / 32768f;
        }
        return samples;
    }

    public static int MsToBytes(int ms) =>
        ms * ISttEngine.SampleRate / 1000 * ISttEngine.BytesPerSample;
}
