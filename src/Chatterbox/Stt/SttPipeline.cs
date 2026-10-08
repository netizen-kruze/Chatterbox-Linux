using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Speech segmentation as the pipeline needs it: SileroVadSegmenter in the
// app, a scripted stand-in in tests.
public interface IVadSegmenter
{
    IReadOnlyList<SpeechSegment> Detect(byte[] pcm, int length);
}

// The capture→text pipeline: PCM chunks come in via Push (bounded channel, so
// the capture callback never blocks and inference never runs on it), a single
// worker task runs Silero VAD segmentation and LocalAgreement partial-commit
// streaming over the ISttEngine, and partial/final text comes out via events.
//
// Utterance logic per VAD tick over the current audio window:
// - no speech: keep only a short pre-roll tail so onsets aren't clipped
// - speech, still running: re-transcribe the window when enough new audio
//   accumulated; words agreed by two consecutive hypotheses become committed;
//   once the window is long, the audio under committed words is trimmed away
//   (bounded re-transcription — MaybeTrim)
// - trailing silence >= UtteranceEndSilenceMs: transcribe once more, emit the
//   final text, and start the next window after the last speech segment
//
// Falling behind: when a pass runs long, everything captured meanwhile waits
// in the queue. The worker takes all of it before the next decision, so one
// VAD tick and one pass cover the backlog instead of several partial ones,
// and the queue holds a full minute so nothing is dropped short of a
// hopeless stall.
public sealed class SttPipeline : IDisposable
{
    public int UtteranceEndSilenceMs { get; init; } = 700;
    public int MinInferIntervalMs { get; init; } = 500;
    public int PrerollMs { get; init; } = 1000;
    public int MaxUtteranceSeconds { get; init; } = 28;
    public int VadTickMs { get; init; } = 300;
    // Bounded re-transcription: past TrimAfterMs of window, the audio under
    // committed words is cut at an engine-reported boundary (a sentence end
    // when one qualifies), always keeping KeepTailMs of recent audio as
    // context. A pass then covers roughly the recent window, so its cost —
    // and the caption lag — stays flat however long a sentence runs.
    public int TrimAfterMs { get; init; } = 10_000;
    public int KeepTailMs { get; init; } = 4_000;
    public int QueueCapacityChunks { get; init; } = 600;   // 100 ms chunks → a minute

    // (committed, pending) — committed text is append-only per utterance.
    public event Action<string, string>? OnPartial;
    public event Action<string>? OnFinal;
    public event Action<bool>? OnSpeechActive;
    public event Action<string>? OnLog;
    // One report per recognition pass — the pace monitor's feed.
    public event Action<SttPassInfo>? OnPass;
    // (trimmed ms, window ms afterwards) — one per trim, for logs and tests.
    public event Action<int, int>? OnTrim;
    // Seconds of audio let go of to catch up (load shedding), one per shed.
    public event Action<int>? OnShed;
    // The worker died on an exception: the session cannot continue and the
    // owner must wind it down — silence here would look like a working
    // session that never hears anything.
    public event Action<Exception>? OnWorkerFailed;

    // Past the longest pass plus this much backlog, audio is dropped rather
    // than queued: every later pass would only get slower and nothing
    // queued would ever be captioned in time.
    public int MaxLagMs { get; init; } = 10_000;

    private readonly ISttEngine _engine;
    private readonly IVadSegmenter _vad;
    private readonly LocalAgreementBuffer _agreement = new();

    private Channel<byte[]>? _channel;
    private CancellationTokenSource? _cts;
    private Task? _worker;
    private Task? _lastWorker;
    private volatile bool _stopRequested;

    private readonly byte[] _window;
    private int _windowBytes;
    private int _bytesSinceVad;
    private int _bytesSinceInfer;
    private bool _speechActive;
    private int _maxWindowMs;
    private int _lastChunkBytes = SttAudio.MsToBytes(100); // capture chunk size, for the backlog estimate

    public SttPipeline(ISttEngine engine, IVadSegmenter vad)
    {
        _engine = engine;
        _vad = vad;
        // Room for the longest window plus a minute of catch-up backlog
        // appended in one go — Append must never have to drop audio.
        _window = new byte[SttAudio.MsToBytes((MaxUtteranceSeconds + 62) * 1000)];
    }

    public bool IsRunning => _worker is { IsCompleted: false };

    public void Start()
    {
        Stop();
        _agreement.Reset();
        _windowBytes = 0;
        _bytesSinceVad = 0;
        _bytesSinceInfer = 0;
        _speechActive = false;
        _stopRequested = false;
        // The engine may cap what one pass can be handed (Whisper on the
        // CPU runs a shortened encoder context).
        _maxWindowMs = MaxUtteranceSeconds * 1000;
        if (_engine.MaxWindowMs > 0) _maxWindowMs = Math.Min(_maxWindowMs, _engine.MaxWindowMs);

        _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacityChunks)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var reader = _channel.Reader;
        _worker = Task.Run(() => WorkerLoopAsync(reader, ct));
    }

    // Called from the capture callback thread; never blocks.
    public void Push(byte[] chunk) => _channel?.Writer.TryWrite(chunk);

    // Returns false when the worker is still inside a pass after the wait:
    // the owner must then defer disposing the engine and VAD (RunAfterWorker).
    public bool Stop()
    {
        if (_worker == null) return true;

        // The worker must be fully joined before anyone disposes the engine
        // or VAD — a lingering worker calling into freed native contexts
        // corrupts the process. Ask it to stop ticking, complete the channel
        // so it drains and flushes, then cancel as a backstop.
        _stopRequested = true;
        _channel?.Writer.TryComplete();
        bool exited;
        try { exited = _worker.Wait(10_000); } catch { exited = true; }
        if (!exited)
        {
            _cts?.Cancel();
            try { exited = _worker.Wait(5_000); } catch { exited = true; }
        }
        if (!exited)
        {
            const string note = "STT pipeline worker did not exit in time; engine/VAD disposal is unsafe";
            OnLog?.Invoke(note);
            ErrorLog.WriteNote("SttPipeline.Stop", note);
        }

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _lastWorker = _worker;
        _worker = null;
        _channel = null;
        return exited;
    }

    // Runs work once the last worker has really returned — now if it has,
    // else right after it does. For freeing what it may still be inside of.
    public void RunAfterWorker(Action work)
    {
        var w = _lastWorker;
        if (w == null || w.IsCompleted) { work(); return; }
        w.ContinueWith(_ => work(), TaskScheduler.Default);
    }

    private async Task WorkerLoopAsync(ChannelReader<byte[]> reader, CancellationToken ct)
    {
        try
        {
            int vadTickBytes = SttAudio.MsToBytes(VadTickMs);
            await foreach (var chunk in reader.ReadAllAsync(ct))
            {
                Take(chunk);
                // Catch-up: whatever arrived while the last pass ran is
                // taken now, so one tick and one pass cover the backlog.
                while (reader.TryRead(out var more)) Take(more);
                if (_stopRequested) continue;   // drain only; the flush below is the last pass
                Shed();
                if (_bytesSinceVad >= vadTickBytes)
                {
                    _bytesSinceVad = 0;
                    await TickAsync(ct);
                }
            }

            // Channel completed (Stop): flush whatever speech is still buffered.
            if (_speechActive && _windowBytes > 0)
                await FinalizeUtteranceAsync(_windowBytes, _windowBytes, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            OnLog?.Invoke($"STT pipeline error: {ex.Message}");
            ErrorLog.WriteEntry("SttPipeline.WorkerLoop", ex);
            if (!_stopRequested) OnWorkerFailed?.Invoke(ex);
        }
    }

    // Load shedding: a pass can only ever cover _maxWindowMs, so audio
    // beyond that plus MaxLagMs would make every later pass slower without
    // ever being captioned in time. Keep the newest window's worth, emit
    // the words already agreed on, and say so.
    private void Shed()
    {
        int limit = SttAudio.MsToBytes(_maxWindowMs + MaxLagMs);
        if (_windowBytes <= limit) return;
        int keep = SttAudio.MsToBytes(_maxWindowMs);
        int dropped = _windowBytes - keep;
        var committed = _agreement.Committed;
        if (committed.Length > 0) OnFinal?.Invoke(committed);
        _agreement.Reset();
        Buffer.BlockCopy(_window, dropped, _window, 0, keep);
        _windowBytes = keep;
        _bytesSinceInfer = 0;
        int seconds = Math.Max(1, dropped / SttAudio.MsToBytes(1000));
        OnShed?.Invoke(seconds);
        OnLog?.Invoke($"[STT] skipped {seconds} s of audio to catch up — recognition is slower than real time");
    }

    private void Take(byte[] chunk)
    {
        if (chunk.Length == 0) return;
        _lastChunkBytes = chunk.Length;
        Append(chunk);
        _bytesSinceVad += chunk.Length;
        _bytesSinceInfer += chunk.Length;
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var segments = _vad.Detect(_window, _windowBytes);

        if (segments.Count == 0)
        {
            if (_speechActive)
            {
                // VAD saw speech before but none now — don't lose the audio.
                await FinalizeUtteranceAsync(_windowBytes, _windowBytes, ct);
                return;
            }
            int preroll = SttAudio.MsToBytes(PrerollMs);
            if (_windowBytes > preroll) TrimWindowKeepingTail(preroll);
            return;
        }

        int windowMs = _windowBytes / SttAudio.MsToBytes(1);
        int lastEndMs = (int)segments[^1].End.TotalMilliseconds;
        bool speaking = windowMs - lastEndMs < UtteranceEndSilenceMs;

        if (speaking)
        {
            SetSpeechActive(true);

            if (windowMs >= _maxWindowMs)
            {
                // Still talking with nothing left to trim: force a cut — in
                // the latest pause when there is a usable one — so latency
                // stays bounded and the engine never sees more than it can
                // hold. The audio after the cut opens the next window.
                int cutBytes = SttAudio.MsToBytes(ForcedCutMs(segments, windowMs));
                await FinalizeUtteranceAsync(cutBytes, cutBytes, ct);
                return;
            }

            if (_bytesSinceInfer >= SttAudio.MsToBytes(MinInferIntervalMs))
            {
                var hypothesis = await TranscribeWindowAsync(_windowBytes, ct);
                if (hypothesis.Text.Length > 0)
                {
                    _agreement.Update(hypothesis.Text);
                    OnPartial?.Invoke(_agreement.Committed, _agreement.Pending);
                    MaybeTrim(hypothesis, segments, windowMs);
                }
            }
            return;
        }

        // Trailing silence long enough: the utterance is over.
        int keepFromByte = Math.Min(_windowBytes, SttAudio.MsToBytes(lastEndMs));
        await FinalizeUtteranceAsync(_windowBytes, keepFromByte, ct);
    }

    // A pause in the second half of the window when there is one, else the
    // cap itself.
    private int ForcedCutMs(IReadOnlyList<SpeechSegment> segments, int windowMs)
    {
        int cut = Math.Min(windowMs, _maxWindowMs);
        for (int i = segments.Count - 1; i >= 0; i--)
        {
            int end = (int)segments[i].End.TotalMilliseconds;
            if (end <= 0 || end > cut) continue;
            if (end >= cut / 2) return end;
            break;
        }
        return cut;
    }

    private void MaybeTrim(SttTranscript hypothesis, IReadOnlyList<SpeechSegment> segments, int windowMs)
    {
        if (windowMs <= TrimAfterMs || hypothesis.Spans.Count == 0) return;
        int cutMs = ChooseCut(hypothesis.Spans, _agreement.ActiveCommittedWordCount, windowMs, KeepTailMs, out int words);
        if (cutMs <= 0 || words <= 0) return;
        cutMs = SnapIntoPause(cutMs, segments);
        int cutBytes = Math.Min(SttAudio.MsToBytes(cutMs), _windowBytes);
        if (cutBytes <= 0) return;

        _agreement.FreezeCommittedPrefix(words);
        int remaining = _windowBytes - cutBytes;
        Buffer.BlockCopy(_window, cutBytes, _window, 0, remaining);
        _windowBytes = remaining;
        int afterMs = remaining / SttAudio.MsToBytes(1);
        OnTrim?.Invoke(cutMs, afterMs);
        OnLog?.Invoke($"[STT] trimmed {cutMs / 1000.0:0.0} s of settled audio ({words} words); window now {afterMs / 1000.0:0.0} s");
    }

    // The latest engine span whose words are all committed and which ends
    // before the kept tail. A sentence end is preferred unless it lies far
    // behind the latest qualifying span. Returns the cut time (0 = none)
    // and the number of committed words the cut covers.
    internal static int ChooseCut(IReadOnlyList<SttSpan> spans, int committedWords, int windowMs, int keepTailMs, out int wordsUpToCut)
    {
        wordsUpToCut = 0;
        int limitMs = windowMs - keepTailMs;
        int cumulative = 0, latest = 0, latestWords = 0, sentence = 0, sentenceWords = 0;
        foreach (var span in spans)
        {
            cumulative += LocalAgreementBuffer.Tokenize(span.Text).Length;
            if (cumulative > committedWords) break;
            if (span.EndMs > limitMs) break;
            if (span.EndMs <= 0) continue;
            latest = span.EndMs;
            latestWords = cumulative;
            if (EndsSentence(span.Text))
            {
                sentence = span.EndMs;
                sentenceWords = cumulative;
            }
        }
        if (sentence > 0 && sentence * 2 >= latest)
        {
            wordsUpToCut = sentenceWords;
            return sentence;
        }
        wordsUpToCut = latestWords;
        return latest;
    }

    private static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        return t.Length > 0 && (t[^1] == '.' || t[^1] == '?' || t[^1] == '!');
    }

    // Engine timestamps are approximate; when the VAD shows a pause close to
    // the cut, cut in the middle of that pause instead, where neither the
    // last settled word nor the next one can be clipped.
    internal static int SnapIntoPause(int cutMs, IReadOnlyList<SpeechSegment> segments, int toleranceMs = 300)
    {
        int prevEnd = 0;
        foreach (var s in segments)
        {
            int start = (int)s.Start.TotalMilliseconds, end = (int)s.End.TotalMilliseconds;
            if (start > prevEnd)
            {
                int mid = (prevEnd + start) / 2;
                if (Math.Abs(mid - cutMs) <= toleranceMs) return mid;
                if (cutMs >= prevEnd && cutMs <= start) return cutMs;   // already inside this pause
            }
            prevEnd = Math.Max(prevEnd, end);
        }
        return cutMs;
    }

    // Every engine call goes through here so each pass is timed: how long
    // it took, how much audio it covered, how much of that was new since
    // the previous pass, and how much captured audio was already queued
    // behind it when it finished (the lag a listener notices).
    private async Task<SttTranscript> TranscribeWindowAsync(int lengthBytes, CancellationToken ct, bool final = false)
    {
        int newAudio = _bytesSinceInfer;
        _bytesSinceInfer = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _engine.TranscribeTimedAsync(_window, lengthBytes, ct);
        sw.Stop();
        if (OnPass != null)
        {
            int bytesPerMs = SttAudio.MsToBytes(1);
            int chunkMs = Math.Max(1, _lastChunkBytes / bytesPerMs);
            int backlogMs = (_channel?.Reader.Count ?? 0) * chunkMs;
            OnPass(new SttPassInfo((int)sw.ElapsedMilliseconds, lengthBytes / bytesPerMs, newAudio / bytesPerMs, backlogMs, final));
        }
        return result;
    }

    // Transcribes the first transcribeBytes, emits the final text, and
    // starts the next window with the audio after consumedBytes.
    private async Task FinalizeUtteranceAsync(int transcribeBytes, int consumedBytes, CancellationToken ct)
    {
        // Never hand the engine more than one pass can hold: the part beyond
        // the cap stays as the next window instead of stretching this pass.
        int cap = SttAudio.MsToBytes(_maxWindowMs);
        if (transcribeBytes > cap)
        {
            transcribeBytes = cap;
            consumedBytes = Math.Min(consumedBytes, cap);
        }
        var final = await TranscribeWindowAsync(transcribeBytes, ct, final: true);
        _agreement.Finalize(final.Text);
        var text = _agreement.Committed;
        if (text.Length > 0) OnFinal?.Invoke(text);

        int residue = _windowBytes - consumedBytes;
        if (residue > 0) Buffer.BlockCopy(_window, consumedBytes, _window, 0, residue);
        _windowBytes = Math.Max(residue, 0);
        _bytesSinceInfer = 0;
        _agreement.Reset();
        SetSpeechActive(false);
    }

    private void Append(byte[] chunk)
    {
        int n = Math.Min(chunk.Length, _window.Length - _windowBytes);
        if (n > 0)
        {
            Buffer.BlockCopy(chunk, 0, _window, _windowBytes, n);
            _windowBytes += n;
        }
    }

    private void TrimWindowKeepingTail(int keepBytes)
    {
        Buffer.BlockCopy(_window, _windowBytes - keepBytes, _window, 0, keepBytes);
        _windowBytes = keepBytes;
    }

    private void SetSpeechActive(bool active)
    {
        if (_speechActive == active) return;
        _speechActive = active;
        OnSpeechActive?.Invoke(active);
    }

    public void Dispose() => Stop();
}
