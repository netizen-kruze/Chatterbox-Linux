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

    private static int _nativeConfigured; // NativeLibraryConfig is per process and must precede the first load
    private LLamaWeights? _model;
    private StatelessExecutor? _executor;
    private ModelParams? _params;

    public string ModelPath { get; }
    public bool UseGpu { get; }
    public int Threads { get; }
    public bool IsLoaded => _executor != null;
    public long LoadMs { get; private set; }

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
            if (Interlocked.Exchange(ref _nativeConfigured, 1) == 0)
            {
                // The packs live under the runtime root (the data folder, like
                // every other native here), not beside the binary — a single
                // file under /opt or ~/Downloads has no writable neighbour.
                NativeLibraryConfig.All
                    .WithSearchDirectory(SttPaths.RuntimeRoot)
                    .WithVulkan(UseGpu)
                    .WithCuda(false)
                    .WithAutoFallback(true)
                    .WithLogCallback((_, _) => { });
            }
            var sw = Stopwatch.StartNew();
            lock (SttAudio.NativeLoadLock)
            {
                _params = new ModelParams(ModelPath)
                {
                    ContextSize = 1024,
                    GpuLayerCount = UseGpu ? 999 : 0,
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
