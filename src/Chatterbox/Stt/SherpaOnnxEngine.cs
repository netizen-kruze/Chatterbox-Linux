using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SherpaOnnx;

namespace Chatterbox.Stt;

// NVIDIA Parakeet TDT via sherpa-onnx (offline transducer). Slots into the
// same batch-transcription pipeline as Whisper (LocalAgreement handles the
// streaming emulation). Model files are the int8 ONNX export in
// <model dir>\parakeet-tdt-0.6b-v2-int8\ — CC-BY-4.0,
// attribution carried in the model catalog and manifest. CPU inference:
// the int8 transducer decodes far faster than realtime without a GPU.
public sealed class SherpaOnnxEngine : ISttEngine
{
    public static string DefaultModelDir => Path.Combine(SttPaths.ModelDir, SttModelCatalog.ParakeetSubdir);

    private readonly string _modelDir;
    private OfflineRecognizer? _recognizer;
    private string[]? _biasTerms;

    public SherpaOnnxEngine(string? modelDir = null)
    {
        _modelDir = modelDir ?? DefaultModelDir;
    }

    public string Name => "Parakeet TDT 0.6B v2 (sherpa-onnx, cpu)";

    public bool IsLoaded => _recognizer != null;

    // Contextual biasing ("hotwords") needs beam-search decoding; both are
    // enabled together at load, and only when the user opted in. If the
    // biased load fails for any reason, the plain greedy load is used.
    public void SetBiasTerms(System.Collections.Generic.IReadOnlyList<string> terms)
    {
        if (_recognizer != null || terms.Count == 0) return;
        _biasTerms = System.Linq.Enumerable.ToArray(terms);
    }

    public bool TryLoad(out string? error)
    {
        error = null;
        if (_recognizer != null) return true;

        foreach (var file in new[] { "encoder.int8.onnx", "decoder.int8.onnx", "joiner.int8.onnx", "tokens.txt" })
        {
            if (!File.Exists(Path.Combine(_modelDir, file)))
            {
                error = $"Parakeet model file missing: {Path.Combine(_modelDir, file)}";
                return false;
            }
        }

        try
        {
            var config = new OfflineRecognizerConfig();
            config.ModelConfig.Transducer.Encoder = Path.Combine(_modelDir, "encoder.int8.onnx");
            config.ModelConfig.Transducer.Decoder = Path.Combine(_modelDir, "decoder.int8.onnx");
            config.ModelConfig.Transducer.Joiner = Path.Combine(_modelDir, "joiner.int8.onnx");
            config.ModelConfig.Tokens = Path.Combine(_modelDir, "tokens.txt");
            config.ModelConfig.ModelType = "nemo_transducer";
            config.ModelConfig.NumThreads = Math.Clamp(Environment.ProcessorCount, 2, 8);
            config.ModelConfig.Provider = "cpu";
            config.DecodingMethod = "greedy_search";

            if (_biasTerms is { Length: > 0 })
            {
                try
                {
                    // One file, overwritten per load — never a growing /tmp.
                    var hotwordsPath = Path.Combine(SttPaths.DataDir, "hotwords.txt");
                    Directory.CreateDirectory(SttPaths.DataDir);
                    File.WriteAllLines(hotwordsPath, _biasTerms);
                    var biased = config;
                    biased.DecodingMethod = "modified_beam_search";
                    biased.HotwordsFile = hotwordsPath;
                    biased.HotwordsScore = 1.5f;
                    lock (SttAudio.NativeLoadLock)
                        _recognizer = new OfflineRecognizer(biased);
                    return true;
                }
                catch
                {
                    _recognizer = null; // fall through to the plain load
                    config.DecodingMethod = "greedy_search";
                    config.HotwordsFile = "";
                }
            }

            lock (SttAudio.NativeLoadLock)
                _recognizer = new OfflineRecognizer(config);
            return true;
        }
        catch (Exception ex)
        {
            _recognizer = null;
            error = $"Parakeet model load failed: {ex.Message}";
            return false;
        }
    }

    public Task<string> TranscribeAsync(byte[] pcm, int length, CancellationToken ct = default) =>
        Task.FromResult(TranscribeTimed(pcm, length).Text);

    // A running native decode cannot be interrupted, but a cancelled session
    // is never handed another one.
    public Task<SttTranscript> TranscribeTimedAsync(byte[] pcm, int length, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(TranscribeTimed(pcm, length));
    }

    // Parakeet's cost grows with the window: cap what one pass is handed
    // (the pipeline keeps the rest as the next window), so a slow CPU never
    // faces a 28 s decode at Stop or at the end of a long sentence.
    public int MaxWindowMs => 20_000;

    private SttTranscript TranscribeTimed(byte[] pcm, int length)
    {
        if (_recognizer == null) throw new InvalidOperationException("Engine not loaded — call TryLoad first.");

        using var stream = _recognizer.CreateStream();
        stream.AcceptWaveform(ISttEngine.SampleRate, SttAudio.ToFloat(pcm, length));
        _recognizer.Decode(stream);
        var result = stream.Result;
        var spans = BuildWordSpans(result.Tokens, result.Timestamps);
        return spans.Count > 0
            ? new SttTranscript(string.Join(' ', spans.Select(s => s.Text)), spans)
            : new SttTranscript(result.Text.Trim(), SttTranscript.NoSpans);
    }

    // Parakeet's tokens are SentencePiece pieces — U+2581 opens a word — with
    // a start time each. Words are rebuilt from them; a word ends where the
    // next begins, the last one a beat after its final piece. Any mismatch
    // between tokens and times means no timing at all, never wrong timing.
    internal static List<SttSpan> BuildWordSpans(string[]? tokens, float[]? timestamps)
    {
        const char WordMark = (char)0x2581;
        var spans = new List<SttSpan>();
        if (tokens == null || timestamps == null || tokens.Length == 0 || tokens.Length != timestamps.Length)
            return spans;

        var word = new System.Text.StringBuilder();
        int startMs = 0, lastMs = 0;
        bool open = false;
        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i] ?? "";
            bool opensWord = token.Length > 0 && (token[0] == WordMark || token[0] == ' ');
            var piece = token.TrimStart(WordMark, ' ');
            int ms = (int)(timestamps[i] * 1000);
            if (opensWord && open && word.Length > 0)
            {
                spans.Add(new SttSpan(word.ToString(), startMs, ms));
                word.Clear();
                open = false;
            }
            if (!open) { startMs = ms; open = true; }
            word.Append(piece);
            lastMs = ms;
        }
        if (word.Length > 0) spans.Add(new SttSpan(word.ToString(), startMs, lastMs + 300));
        return spans;
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
