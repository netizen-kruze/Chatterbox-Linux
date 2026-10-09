using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// Bounded re-transcription, end to end through the real pipeline with a
// scripted engine and a VAD that hears speech everywhere: the window gets
// trimmed under settled words, no pass covers more than the recent window,
// and the final text is every word exactly once, in order.
public class SttPipelineTests
{
    // Speech from the first sample to the last.
    private sealed class AlwaysSpeech : IVadSegmenter
    {
        public IReadOnlyList<SpeechSegment> Detect(byte[] pcm, int length)
        {
            int ms = length / SttAudio.MsToBytes(1);
            return ms == 0
                ? Array.Empty<SpeechSegment>()
                : new[] { new SpeechSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(ms)) };
        }
    }

    // The audio carries its own clock: every 16-bit sample holds the index
    // of its 100 ms chunk, so the engine can tell which words are in any
    // slice it is handed. One word per 500 ms; every fourth ends a sentence.
    private sealed class ScriptedEngine : ISttEngine
    {
        public const int WordMs = 500;
        public int MaxLengthMs;
        public int Calls;
        public string Name => "scripted";
        public bool IsLoaded => true;
        public bool TryLoad(out string? error) { error = null; return true; }
        public void Dispose() { }
        public static string Word(int k) => k % 4 == 3 ? $"w{k}." : $"w{k}";

        public async Task<string> TranscribeAsync(byte[] pcm, int length, CancellationToken ct = default) =>
            (await TranscribeTimedAsync(pcm, length, ct)).Text;

        public Task<SttTranscript> TranscribeTimedAsync(byte[] pcm, int length, CancellationToken ct = default)
        {
            Calls++;
            int lengthMs = length / SttAudio.MsToBytes(1);
            MaxLengthMs = Math.Max(MaxLengthMs, lengthMs);
            int startMs = (pcm[0] | (pcm[1] << 8)) * 100;
            int endMs = startMs + lengthMs;
            var spans = new List<SttSpan>();
            for (int k = (startMs + WordMs - 1) / WordMs; (k + 1) * WordMs <= endMs; k++)
                spans.Add(new SttSpan(Word(k), k * WordMs - startMs, (k + 1) * WordMs - startMs));
            return Task.FromResult(new SttTranscript(string.Join(' ', spans.Select(s => s.Text)), spans));
        }
    }

    private static byte[] Chunk(int index)
    {
        var chunk = new byte[SttAudio.MsToBytes(100)];
        for (int i = 0; i < chunk.Length; i += 2)
        {
            chunk[i] = (byte)(index & 0xFF);
            chunk[i + 1] = (byte)(index >> 8);
        }
        return chunk;
    }

    // Speech until told otherwise, then silence: the utterance closes the
    // way a real pause closes it (Stop no longer transcribes what is left).
    private sealed class SpeechUntilSilenced : IVadSegmenter
    {
        public volatile bool Silent;
        public IReadOnlyList<SpeechSegment> Detect(byte[] pcm, int length)
        {
            int ms = length / SttAudio.MsToBytes(1);
            return Silent || ms == 0
                ? Array.Empty<SpeechSegment>()
                : new[] { new SpeechSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(ms)) };
        }
    }

    [Fact]
    public async Task LongSpeechIsTrimmedAndEveryWordArrivesExactlyOnce()
    {
        var engine = new ScriptedEngine();
        var vad = new SpeechUntilSilenced();
        using var pipeline = new SttPipeline(engine, vad)
        {
            TrimAfterMs = 4000,
            KeepTailMs = 1500,
            MinInferIntervalMs = 500,
            VadTickMs = 300,
            MaxUtteranceSeconds = 120,
        };
        var trims = new List<(int cut, int after)>();
        var finals = new List<string>();
        pipeline.OnTrim += (cut, after) => trims.Add((cut, after));
        pipeline.OnFinal += t => finals.Add(t);

        pipeline.Start();
        const int chunks = 300;   // 30 s of continuous speech
        for (int i = 0; i < chunks; i++)
        {
            pipeline.Push(Chunk(i));
            if (i % 3 == 2) await Task.Delay(1);   // arrives roughly in real-time order
        }
        await Task.Delay(100);
        // The speaker stops: a tick's worth of silence closes the utterance
        // (its 300 ms completes no further word), and the tail arrives as
        // the final.
        vad.Silent = true;
        for (int i = chunks; i < chunks + 3; i++) pipeline.Push(Chunk(i));
        for (int i = 0; i < 100 && finals.Count == 0; i++) await Task.Delay(50);
        pipeline.Stop();

        Assert.NotEmpty(trims);
        Assert.All(trims, t => Assert.True(t.after >= 1500, $"window after trim {t.after} ms"));
        Assert.True(engine.MaxLengthMs <= 8000, $"a pass covered {engine.MaxLengthMs} ms");
        Assert.Single(finals);
        var expected = string.Join(' ', Enumerable.Range(0, chunks * 100 / ScriptedEngine.WordMs).Select(ScriptedEngine.Word));
        Assert.Equal(expected, finals[0]);
    }

    [Fact]
    public void ChooseCutPrefersASentenceEndAmongSettledSpans()
    {
        var spans = new[]
        {
            new SttSpan("one two", 0, 1000), new SttSpan("three.", 1000, 2000),
            new SttSpan("four five", 2000, 3000), new SttSpan("six", 3000, 4000),
        };
        int cut = SttPipeline.ChooseCut(spans, committedWords: 5, windowMs: 6000, keepTailMs: 2000, out int words);
        Assert.Equal(2000, cut);
        Assert.Equal(3, words);
    }

    [Fact]
    public void ChooseCutTakesTheLatestSettledSpanWhenTheSentenceEndIsFarBehind()
    {
        var spans = new[]
        {
            new SttSpan("one.", 0, 1000), new SttSpan("two three", 1000, 3000),
            new SttSpan("four five", 3000, 5000), new SttSpan("six", 5000, 6000),
        };
        int cut = SttPipeline.ChooseCut(spans, committedWords: 5, windowMs: 9000, keepTailMs: 2000, out int words);
        Assert.Equal(5000, cut);
        Assert.Equal(5, words);
    }

    [Fact]
    public void ChooseCutRefusesUnsettledOrRecentAudio()
    {
        var spans = new[] { new SttSpan("one two", 0, 1000), new SttSpan("three", 1000, 2000) };
        Assert.Equal(0, SttPipeline.ChooseCut(spans, committedWords: 1, windowMs: 6000, keepTailMs: 2000, out _));
        Assert.Equal(0, SttPipeline.ChooseCut(spans, committedWords: 3, windowMs: 2500, keepTailMs: 2000, out _));
    }

    [Fact]
    public void SnapIntoPauseCentersTheCutInANearbyGap()
    {
        var segments = new[]
        {
            new SpeechSegment(TimeSpan.Zero, TimeSpan.FromMilliseconds(1900)),
            new SpeechSegment(TimeSpan.FromMilliseconds(2300), TimeSpan.FromMilliseconds(5000)),
        };
        Assert.Equal(2100, SttPipeline.SnapIntoPause(2000, segments));
        Assert.Equal(4000, SttPipeline.SnapIntoPause(4000, segments));
    }
}
