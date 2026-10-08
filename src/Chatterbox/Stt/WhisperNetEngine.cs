using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace Chatterbox.Stt;

// Whisper engine backed by Whisper.net (whisper.cpp). Model files (ggml
// *.bin) live in the user-local model dir, downloaded and hash-verified by
// SttModelManager — never committed to the repo. Runtime selection (CUDA vs
// CPU) uses Whisper.net's default probe order, which falls back to CPU when
// no CUDA device is available.
public sealed class WhisperNetEngine : ISttEngine
{
    public static string DefaultModelDir => SttPaths.ModelDir;

    // CPU tuning (see TryLoad): an encoder context of 1024 positions covers
    // ~20.5 s of audio; the pipeline is told to hand over no more than this.
    public const int CpuAudioContext = 1024;
    public const int CpuMaxWindowMs = 18_000;

    private readonly string _modelPath;
    private readonly string _language;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private string? _biasPrompt;
    private string _runtime = "";
    private bool _cpuTuned;

    // language: ISO code ("en", "de", …) or "auto" for detection. English-only
    // models (*.en) must use "en".
    public WhisperNetEngine(string modelPath, string language = "en")
    {
        _modelPath = modelPath;
        _language = language;
    }

    // Includes which native runtime whisper.cpp selected (Cuda vs Cpu) once
    // loaded — visible in logs so GPU users can confirm acceleration.
    public string Name => _runtime.Length > 0
        ? $"Whisper.net ({Path.GetFileNameWithoutExtension(_modelPath)}, {_runtime})"
        : $"Whisper.net ({Path.GetFileNameWithoutExtension(_modelPath)})";

    public bool IsLoaded => _processor != null;

    public int MaxWindowMs => _cpuTuned ? CpuMaxWindowMs : 0;

    // Whisper's initial prompt conditions decoding — listing nearby player
    // names makes them likely spellings. Applied at load; pre-load only.
    public void SetBiasTerms(IReadOnlyList<string> terms)
    {
        if (_processor != null || terms.Count == 0) return;
        _biasPrompt = "Nearby player names: " + string.Join(", ", terms) + ".";
    }

    public bool TryLoad(out string? error)
    {
        error = null;
        if (_processor != null) return true;

        if (!File.Exists(_modelPath))
        {
            error = $"Whisper model not found at '{_modelPath}'";
            return false;
        }

        try
        {
            lock (SttAudio.NativeLoadLock)
            {
                _factory = WhisperFactory.FromPath(_modelPath);
                _runtime = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "";
                var builder = _factory.CreateBuilder()
                    .WithThreads(Math.Clamp(Environment.ProcessorCount, 2, 8));
                builder = _language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(_language);
                if (_biasPrompt != null) builder = builder.WithPrompt(_biasPrompt);

                // On the CPU the encoder dominates every pass, and its cost
                // follows the context length, not the audio: whisper pads to
                // 30 s. The pipeline keeps live windows under 20 s (bounded
                // re-transcription), so a 1024-position context (~20.5 s)
                // loses nothing and cuts encoder work by roughly a third.
                // Temperature fallback re-decodes a doubtful pass up to five
                // more times — a latency cliff nobody wants live; the next
                // pass corrects a bad one anyway.
                _cpuTuned = _runtime.Contains("Cpu", StringComparison.OrdinalIgnoreCase);
                if (_cpuTuned)
                    builder = builder.WithAudioContextSize(CpuAudioContext).WithTemperatureInc(0f);

                _processor = builder.Build();
                if (_runtime.Length == 0)
                    _runtime = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary?.ToString() ?? "";
            }
            return true;
        }
        catch (Exception ex)
        {
            DisposeCore();
            error = $"Whisper model load failed: {ex.Message}";
            error += AvxNote();
            return false;
        }
    }

    public async Task<string> TranscribeAsync(byte[] pcm, int length, CancellationToken ct = default) =>
        (await TranscribeTimedAsync(pcm, length, ct).ConfigureAwait(false)).Text;

    // Whisper's segments carry start/end times — the pipeline cuts settled
    // audio at their ends. Text is the join of the cleaned segments, so its
    // word count and the spans' always agree.
    public async Task<SttTranscript> TranscribeTimedAsync(byte[] pcm, int length, CancellationToken ct = default)
    {
        if (_processor == null) throw new InvalidOperationException("Engine not loaded — call TryLoad first.");

        var samples = SttAudio.ToFloat(pcm, length);
        var spans = new List<SttSpan>();
        await foreach (var segment in _processor.ProcessAsync(samples, ct))
        {
            var text = CleanHypothesis(segment.Text);
            if (text.Length == 0) continue;
            spans.Add(new SttSpan(text, (int)segment.Start.TotalMilliseconds, (int)segment.End.TotalMilliseconds));
        }
        return new SttTranscript(string.Join(' ', spans.Select(s => s.Text)), spans);
    }

    // Whisper emits bracketed non-speech annotations ([BLANK_AUDIO], [MUSIC],
    // [NOISE], ...) which must never reach the chatbox. Strips [...] spans and
    // music notes, collapses the leftover whitespace.
    public static string CleanHypothesis(string text)
    {
        if (text.IndexOf('[') < 0 && text.IndexOf('♪') < 0) return text.Trim();

        var sb = new StringBuilder(text.Length);
        int depth = 0;
        foreach (var c in text)
        {
            if (c == '[') { depth++; continue; }
            if (c == ']') { if (depth > 0) depth--; continue; }
            if (depth > 0 || c == '♪') continue;
            sb.Append(c);
        }
        var parts = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }

    // The bundled whisper.cpp build is compiled for AVX2; on a CPU without it
    // the load fails in a way that looks like a bad model file. Say why.
    internal static string AvxNote() =>
        System.Runtime.Intrinsics.X86.Avx2.IsSupported
            ? ""
            : " — this CPU has no AVX2, which the bundled Whisper build needs; the Parakeet engine runs without it";

    private void DisposeCore()
    {
        try { _processor?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
        _processor = null;
        _factory?.Dispose();
        _factory = null;
    }

    public void Dispose() => DisposeCore();
}
