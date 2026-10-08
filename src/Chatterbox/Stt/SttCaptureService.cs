using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Chatterbox.Stt;

// Microphone capture on Linux: a recorder child process (pw-record on
// PipeWire, else parec, else arecord) streams raw 16 kHz mono 16-bit PCM
// to its stdout, read here in 100 ms chunks. PipeWire and PulseAudio
// capture is shared by nature, so VRChat keeps the same microphone. The
// device is chosen by name (SttAudioDevices) and passed as the recorder's
// target; the system default needs no target at all.
public sealed class SttCaptureService : IDisposable
{
    // Copies of each capture chunk (16 kHz mono 16-bit PCM). Raised on the
    // reader thread — handlers must only hand the chunk off (e.g.
    // SttPipeline.Push) and never do inference or UI work here.
    public event Action<byte[]>? OnChunk;
    public event Action<string>? OnLog;
    // Raised only for unexpected stops (device unplugged, PipeWire
    // restarted, recorder killed) — Stop() marks its own teardown first, so
    // a normal stop never reaches here.
    public event Action<Exception?>? OnUnexpectedStop;

    public const int ChunkBytes = ISttEngine.SampleRate / 10 * ISttEngine.BytesPerSample; // 100 ms
    // A recorder that is installed but cannot capture (no daemon, unknown
    // target) exits at once; this long is waited to let the next one try.
    private const int EarlyExitProbeMs = 400;
    // A recorder that runs but delivers no bytes is one with nothing to
    // record from (no microphone, a stream PipeWire unlinked when the device
    // vanished mid-session): a live microphone delivers zeros even when
    // muted, so "no bytes for this long" is a dead stream. Checked
    // periodically; the recorder is stopped and the failure reported.
    private const int NoDataProbeMs = 5000;

    private Process? _proc;
    private Thread? _reader;
    private System.Threading.Timer? _noDataProbe;
    private long _bytesRead;
    private long _bytesAtLastProbe;
    private volatile bool _noData;
    private volatile bool _stopping;
    private volatile float _meterLevel;

    public float MeterLevel => _meterLevel;
    public bool IsRunning => _proc != null;
    // Which recorder is running ("pw-record", "parec", "arecord"), for logs.
    public string Backend { get; private set; } = "";

    public void Start(int deviceIndex)
    {
        Stop();

        var target = SttAudioDevices.TargetFor(deviceIndex);
        var failures = new List<string>();
        foreach (var cmd in SttAudioDevices.CaptureCommands(target))
        {
            Process p;
            var stderr = new StringBuilder();
            try { p = Launch(cmd, stderr); }
            catch (Exception ex) { failures.Add($"{cmd.Label}: {ex.Message}"); continue; } // not installed

            if (p.WaitForExit(EarlyExitProbeMs))
            {
                failures.Add($"{cmd.Label}: exited with code {p.ExitCode}" + Tail(stderr));
                p.Dispose();
                continue;
            }

            _proc = p;
            _stopping = false;
            _noData = false;
            Interlocked.Exchange(ref _bytesRead, 0);
            _bytesAtLastProbe = 0;
            Backend = cmd.Label;
            _noDataProbe = new System.Threading.Timer(_ =>
            {
                if (_stopping) return;
                long seen = Interlocked.Read(ref _bytesRead);
                if (seen > _bytesAtLastProbe) { _bytesAtLastProbe = seen; return; }
                _noData = true;
                try { p.Kill(entireProcessTree: true); } catch { }
            }, null, NoDataProbeMs, NoDataProbeMs);
            _reader = new Thread(() => ReadLoop(p, stderr)) { IsBackground = true, Name = "Chatterbox capture" };
            _reader.Start();
            OnLog?.Invoke($"STT capture: {cmd.Label}, {(target == null ? "system default input" : "target " + target)}");
            return;
        }

        throw new InvalidOperationException(failures.Count == 0
            ? "no recorder found — install pipewire-utils (pw-record) or pulseaudio-utils (parec)"
            : "no recorder could open the microphone: " + string.Join("; ", failures));
    }

    private static Process Launch(SttAudioDevices.CaptureCommand cmd, StringBuilder stderr)
    {
        var psi = new ProcessStartInfo(cmd.File)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in cmd.Args) psi.ArgumentList.Add(a);
        LinuxHost.StripSteamPreload(psi);
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"{cmd.File} did not start");
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (stderr) if (stderr.Length < 4000) stderr.AppendLine(e.Data);
        };
        p.BeginErrorReadLine();
        return p;
    }

    private void ReadLoop(Process p, StringBuilder stderr)
    {
        var stream = p.StandardOutput.BaseStream;
        var buf = new byte[ChunkBytes];
        bool eof = false;
        try
        {
            while (!_stopping && !eof)
            {
                int filled = 0;
                while (filled < buf.Length)
                {
                    int n = stream.Read(buf, filled, buf.Length - filled);
                    if (n <= 0) { eof = true; break; }
                    Interlocked.Add(ref _bytesRead, n);
                    filled += n;
                }
                if (eof) break;
                UpdateMeter(buf);
                OnChunk?.Invoke((byte[])buf.Clone());
            }
        }
        catch (Exception ex)
        {
            if (_stopping) return;
            OnLog?.Invoke($"STT capture read error: {ex.Message}");
            _meterLevel = 0f;
            OnUnexpectedStop?.Invoke(ex);
            return;
        }
        if (_stopping) return;

        // EOF while running: the recorder died under us — or was stopped by
        // the probe above because nothing ever arrived.
        try { p.WaitForExit(1000); } catch { }
        string why = _noData
            ? $"{Backend} delivered no audio for {NoDataProbeMs / 1000} s — is the microphone still connected and selected as the input?"
            : $"{Backend} stopped unexpectedly";
        try { if (!_noData && p.HasExited) why += $" (exit code {p.ExitCode})"; } catch { }
        if (!_noData) why += Tail(stderr);
        OnLog?.Invoke("STT capture stopped with error: " + why);
        _meterLevel = 0f;
        OnUnexpectedStop?.Invoke(new IOException(why));
    }

    private static string Tail(StringBuilder stderr)
    {
        string text;
        lock (stderr) text = stderr.ToString();
        text = text.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.Length == 0) return "";
        if (text.Length > 200) text = "…" + text[^200..];
        return " — " + text;
    }

    public void Stop()
    {
        var p = _proc;
        if (p == null) return;
        _stopping = true;
        _proc = null;
        _noDataProbe?.Dispose();
        _noDataProbe = null;

        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
        try { p.WaitForExit(2000); } catch { }
        // The failure path reaches here on the reader thread itself: a
        // thread cannot join itself, it would only wait out the timeout.
        if (_reader != null && _reader != Thread.CurrentThread) _reader.Join(2000);
        _reader = null;
        try { p.Dispose(); } catch { }

        _meterLevel = 0f;
        Backend = "";
    }

    // Block RMS drives the UI meter; the gain lifts normal speech into the
    // upper half of the bar (value chosen by ear during development).
    private const float MeterGain = 6f;

    private void UpdateMeter(ReadOnlySpan<byte> block)
    {
        int sampleCount = block.Length / 2;
        if (sampleCount == 0) { _meterLevel = 0f; return; }
        double energy = 0;
        for (int at = 0; at + 1 < block.Length; at += 2)
        {
            float normalized = unchecked((short)(block[at] | (block[at + 1] << 8))) / 32768f;
            energy += normalized * normalized;
        }
        _meterLevel = MathF.Min(1f, MathF.Sqrt((float)(energy / sampleCount)) * MeterGain);
    }

    public void Dispose() => Stop();
}
