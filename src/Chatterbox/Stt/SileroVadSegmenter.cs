using System;
using System.Collections.Generic;
using System.IO;
using Whisper.net;

namespace Chatterbox.Stt;

public readonly record struct SpeechSegment(TimeSpan Start, TimeSpan End);

// Silero VAD via whisper.cpp's bundled implementation (Whisper.net 1.9 —
// WhisperVadProcessor), so no extra native dependency. The ggml VAD model
// (~0.9 MB) lives in the user-local model dir like the Whisper weights and is
// never committed. Not thread-safe: call Detect from the STT worker only.
public sealed class SileroVadSegmenter : IVadSegmenter, IDisposable
{
    // The file name comes from the catalog entry (SttModelCatalog.Vad), so
    // the download and the load can never disagree about which Silero
    // version this build uses.
    public static string DefaultModelPath => Path.Combine(
        WhisperNetEngine.DefaultModelDir, SttModelCatalog.Vad.PrimaryFileName);

    private readonly string _modelPath;
    private readonly float _threshold;
    private WhisperVadFactory? _factory;
    private WhisperVadProcessor? _processor;

    public SileroVadSegmenter(string? modelPath = null, float threshold = 0.5f)
    {
        _modelPath = modelPath ?? DefaultModelPath;
        _threshold = threshold;
    }

    public bool IsLoaded => _processor != null;

    public bool TryLoad(out string? error)
    {
        error = null;
        if (_processor != null) return true;

        if (!File.Exists(_modelPath))
        {
            error = $"Silero VAD model not found at '{_modelPath}'";
            return false;
        }

        try
        {
            lock (SttAudio.NativeLoadLock)
            {
                _factory = WhisperVadFactory.FromPath(_modelPath);
                _processor = _factory.CreateBuilder()
                    .WithThreshold(_threshold)
                    .WithMinSilenceDuration(TimeSpan.FromMilliseconds(100))
                    .WithSpeechPadding(TimeSpan.FromMilliseconds(30))
                    .Build();
            }
            return true;
        }
        catch (Exception ex)
        {
            Dispose();
            error = $"Silero VAD load failed: {ex.Message}";
            error += WhisperNetEngine.AvxNote();
            return false;
        }
    }

    // Detects speech segments in "length" bytes of 16 kHz mono 16-bit PCM.
    // Stateless per call (the processor state is reset by DetectSpeech).
    public IReadOnlyList<SpeechSegment> Detect(byte[] pcm, int length)
    {
        if (_processor == null) throw new InvalidOperationException("VAD not loaded — call TryLoad first.");

        // Silero operates on 512-sample windows; ignore sub-100 ms buffers.
        if (length < SttAudio.MsToBytes(100)) return Array.Empty<SpeechSegment>();

        var raw = _processor.DetectSpeech(SttAudio.ToFloat(pcm, length));
        var segments = new SpeechSegment[raw.Count];
        for (int i = 0; i < raw.Count; i++)
            segments[i] = new SpeechSegment(raw[i].Start, raw[i].End);
        return segments;
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
    }
}
