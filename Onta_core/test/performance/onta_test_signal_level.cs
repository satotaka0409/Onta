using Onta.Performance;
using Xunit;

namespace Onta.Core.Tests.Performance;

/// <summary>
/// 性能測定の無音判定（ワウ・リサージュの停止条件）です。
/// </summary>
public sealed class OntaTestPerformanceSignalLevel
{
    /// <summary>
    /// −60 dBFS 未満の正弦波・DC だけの入力・空の入力を無音と判定することを確認します。
    /// </summary>
    [Fact]
    public void IsSilent_BelowThresholdOrDcOnly_ReturnsTrue()
    {
        Assert.True(PerformanceSignalLevel.IsSilent(Sine(2048, 1000, 44100, amplitude: 1e-3)));
        Assert.True(PerformanceSignalLevel.IsSilent(Enumerable.Repeat(0.05, 2048).ToArray()));
        Assert.True(PerformanceSignalLevel.IsSilent(ReadOnlySpan<double>.Empty));
    }

    /// <summary>
    /// −60 dBFS 以上の正弦波を無音と判定しないことを確認します。
    /// </summary>
    [Fact]
    public void IsSilent_AboveThreshold_ReturnsFalse()
    {
        Assert.False(PerformanceSignalLevel.IsSilent(Sine(2048, 1000, 44100, amplitude: 2e-3)));
        Assert.False(PerformanceSignalLevel.IsSilent(Sine(2048, 1000, 44100, amplitude: 0.5)));
    }

    /// <summary>
    /// 信号の始まり・終わりを含む区間（端だけトーンで残りが雑音）は信号が続いていないと判定することを確認します。
    /// </summary>
    /// <param name="toneSamples">先頭からトーンが続くサンプル数（以降は雑音のみ）。</param>
    [Theory]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(1700)]
    public void IsSteady_ToneEndsInsideWindow_ReturnsFalse(int toneSamples)
    {
        var pcm = Noise(2048, rms: 1e-3, seed: 1);
        var tone = Sine(toneSamples, 3000, 48000, amplitude: 0.5);
        for (var i = 0; i < toneSamples; i++)
        {
            pcm[i] += tone[i];
        }

        Assert.False(PerformanceSignalLevel.IsSteady(pcm));
        Array.Reverse(pcm);
        Assert.False(PerformanceSignalLevel.IsSteady(pcm));
    }

    /// <summary>
    /// 雑音の乗った連続トーン・広帯域の連続信号（OFDM 相当）は信号が続いていると判定することを確認します。
    /// </summary>
    /// <param name="length">区間長（FFT 長）。</param>
    [Theory]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(8192)]
    public void IsSteady_ContinuousSignal_ReturnsTrue(int length)
    {
        var tone = Sine(length, 3000, 48000, amplitude: 0.3);
        var noise = Noise(length, rms: 3e-3, seed: 2);
        for (var i = 0; i < length; i++)
        {
            tone[i] += noise[i];
        }

        Assert.True(PerformanceSignalLevel.IsSteady(tone));
        Assert.True(PerformanceSignalLevel.IsSteady(Noise(length, rms: 0.1, seed: 3)));
        Assert.False(PerformanceSignalLevel.IsSteady(Noise(length, rms: 5e-4, seed: 4)));
    }

    /// <summary>
    /// 一様乱数の雑音を生成します。
    /// </summary>
    /// <param name="length">サンプル数。</param>
    /// <param name="rms">RMS。</param>
    /// <param name="seed">乱数の種。</param>
    /// <returns>PCM。</returns>
    private static double[] Noise(int length, double rms, int seed)
    {
        var rng = new Random(seed);
        var pcm = new double[length];
        var scale = rms * Math.Sqrt(12.0);
        for (var i = 0; i < length; i++)
        {
            pcm[i] = (rng.NextDouble() - 0.5) * scale;
        }

        return pcm;
    }

    /// <summary>
    /// 正弦波を生成します（RMS は振幅 / √2）。
    /// </summary>
    /// <param name="length">サンプル数。</param>
    /// <param name="frequencyHz">周波数（Hz）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="amplitude">振幅。</param>
    /// <returns>PCM。</returns>
    private static double[] Sine(int length, double frequencyHz, int sampleRate, double amplitude)
    {
        var pcm = new double[length];
        for (var i = 0; i < length; i++)
        {
            pcm[i] = amplitude * Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        }

        return pcm;
    }
}
