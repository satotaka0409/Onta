using Onta.Performance;
using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.Performance;

/// <summary>
/// 性能測定の音声出力で繰り返す変調信号が、先頭へ戻る継ぎ目でもシンボル境界を保つことです。
/// </summary>
public sealed class OntaTestModulatedLoop
{
    /// <summary>データ部 1 シンボルのサンプル数（FFT 256 + CP 16）。</summary>
    private const int SymbolSamples = 256 + 16;

    /// <summary>
    /// 指定秒数以上あり、長さが OFDM シンボルの整数倍（継ぎ目でシンボル境界がずれない）であること。
    /// </summary>
    [Theory]
    [InlineData(16, ModulationScheme.Bpsk)]
    [InlineData(48, ModulationScheme.Qam64)]
    [InlineData(64, ModulationScheme.Qam256)]
    public void Loop_IsWholeSymbolsAndAtLeastRequestedLength(int subcarriers, ModulationScheme modulation)
    {
        const double MinSeconds = 0.5;
        var (left, right) = PerformanceSignalGenerator.GenerateModulatedLoop(subcarriers, modulation, ChannelMode.Stereo, MinSeconds);

        Assert.True(left.Length >= (int)(MinSeconds * PerformanceSignalGenerator.SampleRate));
        Assert.Equal(0, left.Length % SymbolSamples);
        Assert.Equal(left.Length, right.Length);
        Assert.InRange(left.Max(z => Math.Abs(z.Real)), 0.999, 1.001);
    }
}
