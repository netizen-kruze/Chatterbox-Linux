using System;
using System.Threading;

namespace Chatterbox.Stt;

// Rate-limits chatbox text updates: at most one send per interval, and rapid
// updates coalesce so the latest text wins (a trailing send fires when the
// interval elapses). Identical consecutive texts are not re-sent. Thread-safe;
// the send callback runs under the internal lock (keep it fast — a UDP send).
public sealed class CoalescingThrottle : IDisposable
{
    private readonly Action<string> _send;
    private readonly int _intervalMs;
    private readonly object _lock = new();
    private readonly System.Threading.Timer _timer;

    private string? _pending;
    private string? _lastSentText;
    private long _lastSendAt = long.MinValue / 2;
    private bool _scheduled;
    private bool _disposed;

    public CoalescingThrottle(Action<string> send, int intervalMs)
    {
        _send = send;
        _intervalMs = Math.Max(1, intervalMs);
        _timer = new System.Threading.Timer(OnTimer);
    }

    public void Update(string text)
    {
        lock (_lock)
        {
            if (_disposed) return;
            if (text == _lastSentText)
            {
                // Latest state already matches what was sent — drop any stale
                // pending update (latest wins).
                _pending = null;
                return;
            }

            long elapsed = Environment.TickCount64 - _lastSendAt;
            if (!_scheduled && elapsed >= _intervalMs)
            {
                SendLocked(text);
                return;
            }

            _pending = text;
            if (!_scheduled)
            {
                _scheduled = true;
                _timer.Change(Math.Max(0, _intervalMs - elapsed), Timeout.Infinite);
            }
        }
    }

    private void OnTimer(object? _)
    {
        lock (_lock)
        {
            _scheduled = false;
            if (_disposed || _pending == null) return;
            var text = _pending;
            _pending = null;
            if (text == _lastSentText) return;
            SendLocked(text);
        }
    }

    private void SendLocked(string text)
    {
        _lastSendAt = Environment.TickCount64;
        _lastSentText = text;
        try { _send(text); } catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending = null;
        }
        _timer.Dispose();
    }
}
