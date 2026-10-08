using Chatterbox.Stt;
using Xunit;

namespace Chatterbox.Tests;

// Hardware tiers decide the recommended model and the pipeline cadence.
public class SttHardwareTierTests
{
    [Theory]
    [InlineData(true, 4, 16, SttTier.Gpu)]
    [InlineData(true, 8, 4, SttTier.CpuLow)]      // CUDA but too little RAM for the big model
    [InlineData(false, 16, 32, SttTier.CpuHigh)]
    [InlineData(false, 4, 8, SttTier.CpuLow)]
    [InlineData(false, 2, 4, SttTier.CpuMinimal)]
    [InlineData(false, 8, 2, SttTier.CpuMinimal)]
    public void ClassifiesByCudaCoresAndRam(bool cuda, int cores, double ramGb, SttTier expected)
    {
        Assert.Equal(expected, SttHardwareTier.Classify(cuda, cores, ramGb));
    }

    [Fact]
    public void EveryTierHasARecommendationOrAnEmptyOne()
    {
        Assert.Equal("large-v3-turbo-q5", SttHardwareTier.RecommendedModelId(SttTier.Gpu));
        Assert.Equal("base.en", SttHardwareTier.RecommendedModelId(SttTier.CpuHigh));
        Assert.Equal("tiny.en", SttHardwareTier.RecommendedModelId(SttTier.CpuLow));
        Assert.Equal("", SttHardwareTier.RecommendedModelId(SttTier.CpuMinimal));
    }

    [Fact]
    public void DetectIsStableWithinAProcess()
    {
        Assert.Equal(SttHardwareTier.Detect(), SttHardwareTier.Detect());
    }
}
