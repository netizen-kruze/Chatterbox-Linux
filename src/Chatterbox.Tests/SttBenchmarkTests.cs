using System.Text;
using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// The speed check's pure parts: WAV unpacking, word accuracy, verdicts, and
// that the bundled clip is present and in the pipeline's format.
public class SttBenchmarkTests
{
    private static byte[] Wav(byte[] pcm, int extraChunkBytes = 0)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + extraChunkBytes + 8 + pcm.Length);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        if (extraChunkBytes > 0)
        {
            w.Write(Encoding.ASCII.GetBytes("LIST"));
            w.Write(extraChunkBytes);
            w.Write(new byte[extraChunkBytes]);
        }
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);
        return ms.ToArray();
    }

    [Fact]
    public void WavPcmFindsTheDataChunkPastOtherChunks()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
        Assert.Equal(pcm, SttBenchmark.WavPcm(Wav(pcm)));
        Assert.Equal(pcm, SttBenchmark.WavPcm(Wav(pcm, extraChunkBytes: 26)));
        Assert.Throws<InvalidDataException>(() => SttBenchmark.WavPcm(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void TheBundledClipIsFourteenSecondsOfSixteenKilohertzMono()
    {
        var pcm = SttBenchmark.LoadFixturePcm();
        int ms = pcm.Length / SttAudio.MsToBytes(1);
        Assert.InRange(ms, 12_000, 16_000);
    }

    [Fact]
    public void WordAccuracyIsOneHundredMinusWordErrorRate()
    {
        Assert.Equal(100, SttBenchmark.WordAccuracy("The quick brown fox.", "the QUICK brown fox"));
        Assert.Equal(75, SttBenchmark.WordAccuracy("the quick brown cat", "the quick brown fox"));
        Assert.Equal(0, SttBenchmark.WordAccuracy("", "the quick brown fox"));
        Assert.Equal(0, SttBenchmark.WordAccuracy("anything", ""));
    }

    [Theory]
    [InlineData(200, 95, "fast")]
    [InlineData(400, 95, "fast")]
    [InlineData(800, 95, "usable")]
    [InlineData(1500, 95, "too slow")]
    [InlineData(200, 40, "inaccurate")]
    public void VerdictFollowsPassTimeUnlessTheWordsAreWrong(int pass6Ms, int accuracy, string expected)
    {
        Assert.Equal(expected, SttBenchmark.Verdict(pass6Ms, accuracy));
    }
}
