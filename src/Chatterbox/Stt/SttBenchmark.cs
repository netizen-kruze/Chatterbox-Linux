using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Chatterbox.Stt;

public sealed record BenchRow(string Engine, int LoadMs, int Pass6sMs, int PassFullMs, int AccuracyPct, string Verdict, string Note);
public sealed record BenchReport(string Machine, IReadOnlyList<BenchRow> Rows, string Summary);

// The in-app speed check: every installed engine transcribes a bundled
// 14-second speech clip (a synthetic voice reading known text) as a 6 s
// window and in full, timed. Pass time over ~6 s of audio is what live
// captioning keeps repeating, so it decides the verdict; the known text
// gives a word accuracy, so a fast-but-wrong result can't pass. Results go
// to the UI, bench.log and the boot log — the numbers a "it's slow" report
// from another machine needs.
public static class SttBenchmark
{
    public const string FixtureResource = "bench/fixture.wav";
    public const string FixtureText =
        "The quick brown fox jumps over the lazy dog. Chatterbox turns speech into captions on your own computer. " +
        "Nothing you say ever leaves this machine. Please meet me by the fountain at seven tonight.";
    public const int FastPassMs = 400, UsablePassMs = 1000;

    public static byte[] LoadFixturePcm()
    {
        using var s = typeof(SttBenchmark).Assembly.GetManifestResourceStream(FixtureResource)
            ?? throw new InvalidOperationException("speed-check clip missing from the build");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return WavPcm(ms.ToArray());
    }

    // The PCM payload (data chunk) of a RIFF/WAVE file; the clip is 16 kHz
    // mono 16-bit, the pipeline's own format.
    internal static byte[] WavPcm(byte[] wav)
    {
        if (wav.Length < 12 || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F')
            throw new InvalidDataException("not a RIFF file");
        int pos = 12;
        while (pos + 8 <= wav.Length)
        {
            string id = Encoding.ASCII.GetString(wav, pos, 4);
            int size = BitConverter.ToInt32(wav, pos + 4);
            if (id == "data")
            {
                int len = Math.Max(0, Math.Min(size, wav.Length - pos - 8));
                var pcm = new byte[len];
                Buffer.BlockCopy(wav, pos + 8, pcm, 0, len);
                return pcm;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException("no data chunk");
    }

    public static async Task<BenchReport> RunAsync(
        IReadOnlyList<(string Label, Func<ISttEngine> Create)> candidates,
        Action<string>? progress, CancellationToken ct)
    {
        var pcm = LoadFixturePcm();
        int sixBytes = Math.Min(pcm.Length, SttAudio.MsToBytes(6000));
        int warmBytes = Math.Min(pcm.Length, SttAudio.MsToBytes(2000));
        var rows = new List<BenchRow>();

        foreach (var (label, create) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke($"{label}: loading…");
            ISttEngine? engine = null;
            try
            {
                engine = create();
                var sw = Stopwatch.StartNew();
                if (!engine.TryLoad(out var error))
                {
                    rows.Add(new BenchRow(label, (int)sw.ElapsedMilliseconds, 0, 0, 0, "could not load", error ?? ""));
                    continue;
                }
                int loadMs = (int)sw.ElapsedMilliseconds;

                // The first pass pays one-time costs (kernels, caches);
                // live captioning never sees it, so neither does the timing.
                await engine.TranscribeAsync(pcm, warmBytes, ct).ConfigureAwait(false);
                progress?.Invoke($"{label}: timing…");
                sw.Restart();
                await engine.TranscribeAsync(pcm, sixBytes, ct).ConfigureAwait(false);
                int pass6 = (int)sw.ElapsedMilliseconds;
                sw.Restart();
                var text = await engine.TranscribeAsync(pcm, pcm.Length, ct).ConfigureAwait(false);
                int passFull = (int)sw.ElapsedMilliseconds;

                int accuracy = WordAccuracy(text, FixtureText);
                rows.Add(new BenchRow(engine.Name, loadMs, pass6, passFull, accuracy, Verdict(pass6, accuracy), text));
            }
            catch (Exception ex)
            {
                rows.Add(new BenchRow(label, 0, 0, 0, 0, "failed", ex.Message));
            }
            finally { engine?.Dispose(); }
        }

        var best = rows.Where(r => r.Pass6sMs > 0 && r.AccuracyPct >= 60).OrderBy(r => r.Pass6sMs).FirstOrDefault();
        string summary = best == null
            ? "no engine produced a usable result"
            : $"best: {best.Engine} — {best.Pass6sMs} ms per 6 s pass, {best.AccuracyPct}% of words right ({best.Verdict})";
        return new BenchReport(Chatterbox.MachineProfile.Describe(), rows, summary);
    }

    // Pass time over ~6 s of audio against what live captioning needs: the
    // pipeline re-runs a pass every 0.3–0.5 s of speech, so anything much
    // above that lags, and beyond a second it never catches up.
    internal static string Verdict(int pass6Ms, int accuracyPct)
    {
        if (accuracyPct < 60) return "inaccurate";
        if (pass6Ms <= FastPassMs) return "fast";
        if (pass6Ms <= UsablePassMs) return "usable";
        return "too slow";
    }

    // 100 minus the word error rate (edit distance over normalized words).
    internal static int WordAccuracy(string hypothesis, string reference)
    {
        var h = Words(hypothesis);
        var r = Words(reference);
        if (r.Length == 0) return 0;
        var prev = new int[h.Length + 1];
        var cur = new int[h.Length + 1];
        for (int j = 0; j <= h.Length; j++) prev[j] = j;
        for (int i = 1; i <= r.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= h.Length; j++)
                cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1),
                                  prev[j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        int errors = prev[h.Length];
        return Math.Clamp(100 - (int)Math.Round(100.0 * errors / r.Length), 0, 100);
    }

    private static string[] Words(string text) =>
        text.ToLowerInvariant()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => new string(w.Where(char.IsLetterOrDigit).ToArray()))
            .Where(w => w.Length > 0)
            .ToArray();

    public static string FormatReport(BenchReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"speed check {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"machine: {report.Machine}");
        foreach (var r in report.Rows)
            sb.AppendLine($"  {r.Engine}: load {r.LoadMs} ms, 6 s pass {r.Pass6sMs} ms, full clip {r.PassFullMs} ms, " +
                          $"{r.AccuracyPct}% words right — {r.Verdict}" +
                          (r.Verdict is "could not load" or "failed" ? $" ({r.Note})" : ""));
        sb.AppendLine("  " + report.Summary);
        return sb.ToString();
    }
}
