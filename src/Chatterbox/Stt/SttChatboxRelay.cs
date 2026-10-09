using System;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

// Connects a running SttService to the VRChat chatbox: committed partials and
// finals flow into the RollingChatboxBuffer, whose window is sent through the
// CoalescingThrottle (VRChat rate-limits chatbox updates) to the OSC sender.
// While speech is active, the typing indicator is pulsed.
public sealed class SttChatboxRelay : IDisposable
{
    public const int DefaultIntervalMs = 1500;
    public const int MinIntervalMs = 1000;
    private const int TypingPulseMs = 5000;

    // Silence gap after which the next utterance starts on a new chatbox
    // line. Measured between VAD speech-end and the next speech-start; the
    // pipeline has already absorbed ~700 ms of silence before declaring an
    // utterance over, so the 2300 ms default amounts to a ~3-second pause.
    // Settable (like IntervalMs) for hosts that expose it in UI.
    public int NewLineGapMs { get; set; } = 2300;

    // Silence gap after which the window is reset instead: VRChat fades the
    // chatbox bubble out after ~30 s without updates, and once the old text
    // has disappeared in-game it must not resurface under new speech.
    public int ClearGapMs { get; set; } = 30_000;

    // Set before Start; clamped to MinIntervalMs.
    public int IntervalMs
    {
        get => _intervalMs;
        set => _intervalMs = Math.Max(value, MinIntervalMs);
    }
    private int _intervalMs = DefaultIntervalMs;

    public bool TypingIndicator { get; set; } = true;

    // Translation (optional). When set, finished utterances go through it
    // before they reach the chatbox, one at a time and in order; the live
    // (partial) text is withheld from the chatbox meanwhile, because a
    // reader of the target language gains nothing from it — the typing
    // indicator covers the gap. A translation that fails or comes back
    // empty falls back to the original words, so nothing said is lost.
    // Set before Start, or between utterances; cleared by Stop.
    public Func<string, CancellationToken, Task<string?>>? Translator { get; set; }
    public bool ShowOriginal { get; set; }
    public event Action<string, string>? OnTranslated;   // (original, translated)
    private readonly SemaphoreSlim _translateGate = new(1, 1);
    private CancellationTokenSource? _translateCts;

    // Raised after each chatbox text send — the host uses this for the
    // UI's sent-preview.
    public event Action<string>? OnTextSent;
    public event Action<string>? OnLog;

    private readonly SttService _stt;
    private readonly RollingChatboxBuffer _buffer;
    private readonly ChatboxOscSender _osc;
    private CoalescingThrottle? _throttle;
    private System.Threading.Timer? _typingPulse;
    private volatile bool _speechActive;
    private bool _started;
    private long _speechEndedAt;
    private bool _nextOnNewLine;

    private readonly Action<string, string> _onPartial;
    private readonly Action<string> _onFinal;
    private readonly Action<bool> _onSpeechActive;

    // hideBackgroundReserve: reserve 2 chars for hosts that append control
    // glyphs to the chatbox payload.
    public SttChatboxRelay(SttService stt, bool hideBackgroundReserve = false,
        string ip = "127.0.0.1", int port = 9000)
    {
        _stt = stt;
        _buffer = new RollingChatboxBuffer(
            RollingChatboxBuffer.DefaultMaxChars - (hideBackgroundReserve ? 2 : 0));
        _osc = new ChatboxOscSender(ip, port);
        _onPartial = (committed, _) =>
        {
            if (Translator != null) return;
            _buffer.UpdateLive(committed, _nextOnNewLine);
            Push();
        };
        _onFinal = text =>
        {
            if (Translator == null) { _buffer.CommitUtterance(text, _nextOnNewLine); Push(); return; }
            bool onNewLine = _nextOnNewLine;
            _ = TranslateAndCommitAsync(text, onNewLine);
        };
        _onSpeechActive = OnSpeechActiveChanged;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        _throttle = new CoalescingThrottle(SendText, IntervalMs);
        _translateCts = new CancellationTokenSource();
        _typingPulse = new System.Threading.Timer(
            _ => { if (_speechActive && TypingIndicator) _osc.SendTyping(true); },
            null, Timeout.Infinite, Timeout.Infinite);

        _stt.OnPartial += _onPartial;
        _stt.OnFinal += _onFinal;
        _stt.OnSpeechActive += _onSpeechActive;
        OnLog?.Invoke($"[STT] chatbox relay started (interval {IntervalMs} ms, typing {(TypingIndicator ? "on" : "off")})");
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;

        _stt.OnPartial -= _onPartial;
        _stt.OnFinal -= _onFinal;
        _stt.OnSpeechActive -= _onSpeechActive;

        _typingPulse?.Dispose();
        _typingPulse = null;
        try { _translateCts?.Cancel(); } catch { }
        _translateCts?.Dispose();
        _translateCts = null;
        _throttle?.Dispose();
        _throttle = null;

        // Leave the game chatbox clean.
        if (TypingIndicator) _osc.SendTyping(false);
        _osc.SendInput("");
        _buffer.Clear();
        _speechEndedAt = 0;
        _nextOnNewLine = false;
        OnLog?.Invoke("[STT] chatbox relay stopped");
    }

    // One utterance at a time, in arrival order; the gate keeps a slow
    // translation from reordering two quick sentences.
    private async Task TranslateAndCommitAsync(string text, bool onNewLine)
    {
        var translator = Translator;
        var cts = _translateCts;
        if (translator == null || cts == null) return;
        var ct = cts.Token;
        string? translated = null;
        try
        {
            await _translateGate.WaitAsync(ct).ConfigureAwait(false);
            try { translated = await translator(text, ct).ConfigureAwait(false); }
            finally { _translateGate.Release(); }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            ErrorLog.WriteEntry("Translate", ex);
            OnLog?.Invoke($"[STT] translation failed — {ex.Message}");
        }
        if (ct.IsCancellationRequested || !_started) return;
        _buffer.CommitUtterance(ChatboxText(text, translated, ShowOriginal), onNewLine);
        Push();
        if (translated != null) OnTranslated?.Invoke(text, translated);
    }

    // What the chatbox gets for a finished utterance: the translation, the
    // translation with the original in brackets, or — when translation
    // produced nothing — the original words.
    internal static string ChatboxText(string original, string? translated, bool showOriginal) =>
        translated == null ? original : showOriginal ? $"{translated} ({original})" : translated;

    private void Push()
    {
        var window = _buffer.Window;
        if (window.Length > 0) _throttle?.Update(window);
    }

    private void SendText(string text)
    {
        _osc.SendInput(text);
        OnTextSent?.Invoke(text);
    }

    private void OnSpeechActiveChanged(bool active)
    {
        _speechActive = active;

        // A new utterance after a pause goes on a new line; after a gap long
        // enough that the in-game bubble has faded, it starts a fresh window.
        if (active)
        {
            var gap = _speechEndedAt == 0 ? 0 : Environment.TickCount64 - _speechEndedAt;
            if (gap >= ClearGapMs) _buffer.Clear();
            _nextOnNewLine = gap >= NewLineGapMs && gap < ClearGapMs;
        }
        else
            _speechEndedAt = Environment.TickCount64;

        if (!TypingIndicator) return;
        _osc.SendTyping(active);
        try
        {
            _typingPulse?.Change(active ? TypingPulseMs : Timeout.Infinite,
                                 active ? TypingPulseMs : Timeout.Infinite);
        }
        catch (ObjectDisposedException) { } // Stop() raced a speech-state change
    }

    public void Dispose()
    {
        Stop();
        _osc.Dispose();
    }
}
