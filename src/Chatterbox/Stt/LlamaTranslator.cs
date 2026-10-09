using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Chatterbox.Stt;

// Local translation of finished captions with Tencent's Hy-MT2 1.8B model
// through llama.cpp (LLamaSharp). Chosen after a timed bench on
// 2026-10-08 (docs/TRANSLATION_BENCH-2026-10-08.md): the fastest of the
// small open-weight translators that was also accurate, Apache-2.0, 1.1 GB.
// One request at a time, greedy decoding, no network — the model and the
// runtime both live on this machine. Not thread-safe: Translate is
// serialized by the caller (SttChatboxRelay runs one translation at a time).
public sealed class LlamaTranslator : IDisposable
{
    // Languages Hy-MT2 handles well, as shown in Settings. Codes are what
    // settings store; names are what the model is asked for.
    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[]
    {
        ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese (Simplified)"), ("zh-TW", "Chinese (Traditional)"),
        ("es", "Spanish"), ("pt", "Portuguese"), ("fr", "French"), ("de", "German"), ("it", "Italian"),
        ("nl", "Dutch"), ("pl", "Polish"), ("ru", "Russian"), ("uk", "Ukrainian"), ("tr", "Turkish"),
        ("ar", "Arabic"), ("hi", "Hindi"), ("id", "Indonesian"), ("vi", "Vietnamese"), ("th", "Thai"),
        ("sv", "Swedish"), ("cs", "Czech"), ("en", "English"),
    };

    public static string LanguageName(string code)
    {
        foreach (var (c, name) in Languages)
            if (string.Equals(c, code, StringComparison.OrdinalIgnoreCase)) return name;
        return "English";
    }

    // Hy-MT2's documented instruction (the English form of its default
    // template): only the translation comes back, no commentary.
    public static string Instruction(string text, string targetCode) =>
        $"Translate the following text into {LanguageName(targetCode)}. Only output the translated result, without any additional explanation:\n{text}";

    // NativeLibraryConfig is per process and must precede the first load:
    // whether Vulkan was asked for is decided once, by the first translator.
    private static bool? _configuredWithVulkan;
    public static bool NativeConfigured => _configuredWithVulkan != null;
    public static bool ConfiguredWithVulkan => _configuredWithVulkan == true;

    // LLamaSharp detects Vulkan by running `vulkaninfo --summary` — with no
    // such tool on PATH it silently loads the CPU build, however good the
    // GPU and its driver are. Fedora ships it in vulkan-tools.
    public static bool VulkanProbePresent => LinuxHost.ToolOnPath("vulkaninfo") != null;
    public const string VulkanProbeNote =
        "the Vulkan runtime cannot be detected without the vulkaninfo tool (sudo dnf install vulkan-tools)";

    // What the loader said it loaded, from its own log — the one truthful
    // source for "runs on the GPU". Lines are also handed to OnLoaderLog
    // for the boot log, capped.
    public static string? LoadedLibrary { get; private set; }
    public static string LoadedVariant => ParseVariant(LoadedLibrary);
    public static event Action<string>? OnLoaderLog;
    private static int _loaderLines;

    private LLamaWeights? _model;
    private StatelessExecutor? _executor;
    private ModelParams? _params;

    public string ModelPath { get; }
    public bool UseGpu { get; }
    public int Threads { get; }
    public bool IsLoaded => _executor != null;
    public long LoadMs { get; private set; }
    // Why the GPU was asked for but is not doing the work, or null.
    public string? GpuUnavailableReason { get; private set; }
    public bool GpuActive => UseGpu && GpuUnavailableReason == null && LoadedVariant == "vulkan";
    public string Backend => GpuActive ? "Vulkan GPU" : $"CPU ({(LoadedVariant.Length > 0 ? LoadedVariant : "?")}), {Threads} threads";

    // "…/runtimes/linux-x64/native/vulkan/libllama.so" → "vulkan";
    // "…/native/avx2/libllama.so" → "avx2"; anything else → "".
    internal static string ParseVariant(string? loadedPath)
    {
        if (string.IsNullOrEmpty(loadedPath)) return "";
        var p = loadedPath.Replace('\\', '/');
        int i = p.IndexOf("/native/", StringComparison.Ordinal);
        if (i < 0) return "";
        var rest = p[(i + "/native/".Length)..];
        int slash = rest.IndexOf('/');
        return slash < 0 ? "" : rest[..slash];
    }

    // The loader's "Successfully loaded '<path>'" names the library that
    // is now in the process; dependencies and failures are logged too.
    internal static void NoteLoaderLine(string message)
    {
        var m = (message ?? "").Trim();
        if (m.Length == 0) return;
        const string loaded = "Successfully loaded '";
        if (m.StartsWith(loaded, StringComparison.Ordinal) && m.EndsWith("'", StringComparison.Ordinal))
            LoadedLibrary = m[loaded.Length..^1];
        if (!(m.Contains("loaded", StringComparison.OrdinalIgnoreCase) || m.Contains("Failed", StringComparison.Ordinal))) return;
        if (Interlocked.Increment(ref _loaderLines) > 30) return;
        OnLoaderLog?.Invoke(m);
    }

    internal static void ResetForTests() { LoadedLibrary = null; _loaderLines = 0; }

    public LlamaTranslator(string modelPath, bool useGpu, int? threads = null)
    {
        ModelPath = modelPath;
        UseGpu = useGpu;
        Threads = threads ?? Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
    }

    public bool TryLoad(out string? error)
    {
        error = null;
        if (_executor != null) return true;
        if (!File.Exists(ModelPath)) { error = $"translation model not found at '{ModelPath}'"; return false; }
        try
        {
            bool wantGpu = UseGpu;
            if (wantGpu && !VulkanProbePresent)
            {
                GpuUnavailableReason = VulkanProbeNote;
                wantGpu = false;
            }
            lock (SttAudio.NativeLoadLock)
            {
                if (_configuredWithVulkan == null)
                {
                    // The packs live under the runtime root (the data folder, like
                    // every other native here), not beside the binary — a single
                    // file under /opt or ~/Downloads has no writable neighbour.
                    NativeLibraryConfig.All
                        .WithSearchDirectory(SttPaths.RuntimeRoot)
                        .WithVulkan(wantGpu)
                        .WithCuda(false)
                        .WithAutoFallback(true)
                        .WithLogCallback((_, message) => NoteLoaderLine(message));
                    _configuredWithVulkan = wantGpu;
                }
                else if (wantGpu && _configuredWithVulkan == false)
                {
                    // The native library is bound once per process.
                    GpuUnavailableReason = "the translation runtime was already loaded without Vulkan in this run — restart Chatterbox to use the GPU";
                }
            }
            var sw = Stopwatch.StartNew();
            lock (SttAudio.NativeLoadLock)
            {
                _params = new ModelParams(ModelPath)
                {
                    ContextSize = 1024,
                    GpuLayerCount = UseGpu && GpuUnavailableReason == null ? 999 : 0,
                    Threads = Threads,
                    BatchThreads = Threads,
                };
                _model = LLamaWeights.LoadFromFile(_params);
                _executor = new StatelessExecutor(_model, _params);
            }
            // One throwaway sentence now: the first request on a GPU also compiles
            // the shaders (about 3 s on Vulkan), which must not land on a caption.
            TranslateAsync("Hello.", "en").GetAwaiter().GetResult();
            LoadMs = sw.ElapsedMilliseconds;
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            error = $"translation runtime failed to load: {ex.Message}";
            return false;
        }
    }

    // The translated text, or null when the model produced nothing usable.
    public async Task<string?> TranslateAsync(string text, string targetCode, CancellationToken ct = default)
    {
        if (_executor == null || _model == null) throw new InvalidOperationException("translator not loaded — call TryLoad first");
        text = text.Trim();
        if (text.Length == 0) return null;

        var template = new LLamaTemplate(_model);
        template.Add("user", Instruction(text, targetCode));
        template.AddAssistant = true;
        var prompt = Encoding.UTF8.GetString(template.Apply());

        var ip = new InferenceParams
        {
            MaxTokens = Math.Clamp(text.Length * 2 + 24, 48, 240),
            SamplingPipeline = new GreedySamplingPipeline(),
            AntiPrompts = new[] { "<|im_end|>", "<|endoftext|>" },
        };
        var sb = new StringBuilder();
        await foreach (var piece in _executor.InferAsync(prompt, ip, ct).ConfigureAwait(false))
            sb.Append(piece);
        var result = Clean(sb.ToString());
        return result.Length > 0 ? result : null;
    }

    // Model output as caption text: no stop tokens, no stray quotes, one
    // line — a chatbox utterance never spans lines on its own.
    internal static string Clean(string raw)
    {
        var s = raw.Replace("<|im_end|>", "").Replace("<|endoftext|>", "").Trim();
        int think = s.IndexOf("</think>", StringComparison.Ordinal);
        if (think >= 0) s = s[(think + 8)..].Trim();
        s = s.Replace("\r", " ").Replace("\n", " ");
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '「' && s[^1] == '」')))
            s = s[1..^1].Trim();
        return s;
    }

    public void Dispose()
    {
        _executor = null;
        _model?.Dispose();
        _model = null;
    }
}
