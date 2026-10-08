using System;

namespace Chatterbox.Stt;

// Owns the microphone capture and the recognition pipeline for one STT
// session. The host controller subscribes to the events for the UI;
// SttChatboxRelay consumes OnPartial/OnFinal/OnSpeechActive for the chatbox.
public sealed class SttService : IDisposable
{
    public event Action<string>? OnLog;
    public event Action<string, string>? OnPartial;   // (committed, pending)
    public event Action<string>? OnFinal;
    public event Action<bool>? OnSpeechActive;
    public event Action<SttPassInfo>? OnPass;        // per recognition pass (pace)
    // The session died mid-way — the capture (device unplugged, driver
    // failure) or the recognition worker (an engine pass threw); the
    // reason is a full sentence. IsRunning is NOT changed here — the owner
    // decides how to wind down.
    public event Action<string>? OnSessionFailed;

    private readonly SttCaptureService _capture = new();
    private SttPipeline? _pipeline;
    private SileroVadSegmenter? _vad;
    private ISttEngine? _engine;

    public float MeterLevel => _capture.MeterLevel;
    public bool IsRunning { get; private set; }
    public string EngineName => _engine?.Name ?? "";

    // Experimental name recognition: applied to every partial/final before
    // events fire (both UI and chatbox see corrected text). Null when the
    // feature is off — a single null check, nothing else.
    public volatile Func<string, string>? TextFilter;

    // Takes ownership of the engine; it is disposed on Stop().
    // fastHardware tightens the pipeline cadence (more frequent VAD ticks and
    // re-inference, shorter end-of-utterance silence) for GPU/high-CPU tiers.
    public bool Start(int deviceIndex, ISttEngine engine, out string? error, bool fastHardware = false)
    {
        Stop();

        // VAD first: it is the cheap load, so a missing/broken VAD model fails
        // fast before the (potentially large) engine model is loaded.
        _vad = new SileroVadSegmenter();
        if (!_vad.TryLoad(out error))
        {
            _vad.Dispose(); _vad = null;
            engine.Dispose();
            return false;
        }

        if (!engine.IsLoaded && !engine.TryLoad(out error))
        {
            _vad.Dispose(); _vad = null;
            engine.Dispose();
            return false;
        }

        _engine = engine;
        // Fast hardware gets quicker partials (tighter VAD/inference cadence)
        // but the utterance-end threshold stays at the default: 500 ms proved
        // too twitchy — natural mid-sentence pauses cut utterances short.
        _pipeline = fastHardware
            ? new SttPipeline(engine, _vad)
            {
                VadTickMs = 200,
                MinInferIntervalMs = 300,
            }
            : new SttPipeline(engine, _vad)
            {
                // Slower hardware: keep the re-transcribed window shorter
                // still — each pass costs what it covers.
                TrimAfterMs = 8000,
                KeepTailMs = 3000,
            };
        _pipeline.OnPartial += (committed, pending) =>
        {
            var f = TextFilter;
            if (f != null) { committed = f(committed); pending = f(pending); }
            OnPartial?.Invoke(committed, pending);
        };
        _pipeline.OnFinal += text =>
        {
            var f = TextFilter;
            if (f != null) text = f(text);
            OnFinal?.Invoke(text);
        };
        _pipeline.OnSpeechActive += active => OnSpeechActive?.Invoke(active);
        _pipeline.OnPass += p => OnPass?.Invoke(p);
        _pipeline.OnLog += s => OnLog?.Invoke(s);
        _pipeline.OnWorkerFailed += ex => OnSessionFailed?.Invoke($"recognition failed ({ex.Message})");

        _capture.OnChunk += OnCaptureChunk;
        _capture.OnLog += OnCaptureLog;
        _capture.OnUnexpectedStop += OnCaptureStopped;

        _pipeline.Start();
        try
        {
            _capture.Start(deviceIndex);
        }
        catch (Exception ex)
        {
            error = $"STT capture failed to start: {ex.Message}";
            Stop();
            return false;
        }

        IsRunning = true;
        OnLog?.Invoke($"[STT] started ({engine.Name}, device {deviceIndex})");
        error = null;
        return true;
    }

    public void Stop()
    {
        if (_pipeline == null && !_capture.IsRunning) return;

        // Unsubscribe the failure hook first: a capture teardown must never
        // masquerade as a mid-session device failure.
        _capture.OnUnexpectedStop -= OnCaptureStopped;
        _capture.Stop();
        _capture.OnChunk -= OnCaptureChunk;
        _capture.OnLog -= OnCaptureLog;

        bool workerExited = _pipeline?.Stop() ?? true;
        var pipeline = _pipeline; _pipeline = null;
        var vad = _vad; _vad = null;
        var engine = _engine; _engine = null;
        if (workerExited || pipeline == null)
        {
            vad?.Dispose();
            engine?.Dispose();
        }
        else
        {
            // The worker is still inside a native pass: freeing the engine
            // under it would corrupt the process. Both are freed the moment
            // the worker returns (or stay allocated if it never does —
            // better than a crash).
            OnLog?.Invoke("[STT] engine disposal deferred until the running recognition pass returns");
            pipeline.RunAfterWorker(() =>
            {
                try { vad?.Dispose(); } catch { }
                try { engine?.Dispose(); } catch { }
            });
        }

        if (IsRunning) OnLog?.Invoke("[STT] stopped");
        IsRunning = false;
    }

    private void OnCaptureChunk(byte[] chunk) => _pipeline?.Push(chunk);
    private void OnCaptureLog(string msg) => OnLog?.Invoke(msg);
    private void OnCaptureStopped(Exception? ex) =>
        OnSessionFailed?.Invoke($"microphone capture failed ({ex?.Message ?? "device lost"})");

    public void Dispose() => Stop();
}
