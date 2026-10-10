using Onta.Performance;
using Xunit;

namespace Onta.Core.Tests.Performance;

/// <summary>
/// 性能測定ワウの送信周波数ロックです。
/// </summary>
public sealed class OntaTestWowReference
{
    [Fact]
    public void FindNearest_1005Hz_SnapsTo1kHz()
    {
        var nearest = PerformanceWowReference.FindNearest(
            1005,
            PerformanceWowReference.ToneFrequenciesHz);
        Assert.Equal(1000, nearest);
    }

    [Fact]
    public void TryLock_HoldsThroughOnePercentWow()
    {
        var locked = 0.0;
        Assert.True(PerformanceWowReference.TryLock(
            1000,
            PerformanceWowReference.ToneFrequenciesHz,
            ref locked));
        Assert.Equal(1000, locked);

        Assert.True(PerformanceWowReference.TryLock(
            1010,
            PerformanceWowReference.ToneFrequenciesHz,
            ref locked));
        Assert.Equal(1000, locked);
        Assert.InRange(PerformanceWowReference.ToWowPercent(1010, locked), 0.9, 1.1);
    }

    [Fact]
    public void TryLock_RelocksWhenToneJumps()
    {
        var locked = 1000.0;
        Assert.True(PerformanceWowReference.TryLock(
            8000,
            PerformanceWowReference.ToneFrequenciesHz,
            ref locked));
        Assert.Equal(8000, locked);
    }

    [Fact]
    public void InterpolatePeakOffset_SymmetricNeighbors_IsZero()
    {
        Assert.Equal(0, PerformanceWowReference.InterpolatePeakOffset(1, 2, 1), 6);
    }

    /// <summary>
    /// 揺れの無い正弦波は、ビン間のどこにあってもワウ 0.005% 以内で測れること（FFT ビンの放物線補間では最大で約 0.1% ずれていた）。
    /// </summary>
    [Theory]
    [InlineData(48000, 2048)]
    [InlineData(48000, 1024)]
    [InlineData(44100, 4096)]
    [InlineData(44100, 8192)]
    public void RefinePeakFrequency_SteadyTone_HasNoWowBias(int sampleRate, int fftSize)
    {
        var windowed = new double[fftSize];
        var worstParabolic = 0.0;
        foreach (var toneHz in PerformanceWowReference.ToneFrequenciesHz)
        {
            if (toneHz >= sampleRate * 0.45)
            {
                continue;
            }

            var pcm = new double[fftSize];
            for (var i = 0; i < fftSize; i++)
            {
                pcm[i] = 0.5 * Math.Sin((2.0 * Math.PI * toneHz * i / sampleRate) + 0.3);
            }

            var coarseHz = ParabolicPeakHz(pcm, sampleRate);
            worstParabolic = Math.Max(worstParabolic, Math.Abs(PerformanceWowReference.ToWowPercent(coarseHz, toneHz)));
            var refinedHz = PerformanceWowReference.RefinePeakFrequencyHz(
                pcm,
                sampleRate,
                coarseHz,
                sampleRate / (double)fftSize,
                windowed);
            Assert.InRange(PerformanceWowReference.ToWowPercent(refinedHz, toneHz), -0.005, 0.005);
        }

        Assert.True(worstParabolic > 0.01, $"放物線補間の偏り {worstParabolic:F4}%");
    }

    /// <summary>
    /// 0.3% 速い正弦波は、ワウ +0.3% として測れること。
    /// </summary>
    [Fact]
    public void RefinePeakFrequency_OffsetTone_MeasuresOffset()
    {
        const int sampleRate = 48000;
        const int fftSize = 2048;
        const double toneHz = 1003.0;
        var pcm = new double[fftSize];
        for (var i = 0; i < fftSize; i++)
        {
            pcm[i] = Math.Sin(2.0 * Math.PI * toneHz * i / sampleRate);
        }

        var refinedHz = PerformanceWowReference.RefinePeakFrequencyHz(
            pcm,
            sampleRate,
            ParabolicPeakHz(pcm, sampleRate),
            sampleRate / (double)fftSize,
            new double[fftSize]);
        Assert.InRange(PerformanceWowReference.ToWowPercent(refinedHz, 1000), 0.295, 0.305);
    }

    /// <summary>
    /// 単一トーンはどの窓関数・FFT 長でも単一トーン、OFDM のキャリア群は単一トーンでないと判定すること。
    /// </summary>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="fftSize">FFT 長。</param>
    [Theory]
    [InlineData(48000, 1024)]
    [InlineData(48000, 2048)]
    [InlineData(44100, 4096)]
    [InlineData(44100, 8192)]
    public void IsSingleTone_ToneVsOfdmCarriers(int sampleRate, int fftSize)
    {
        var rng = new Random(5);
        foreach (var window in Enum.GetValues<PerformanceFftWindowKind>())
        {
            foreach (var toneHz in new[] { 315.0, 3000.0, 3011.0, 15000.0 })
            {
                var tone = new double[fftSize];
                for (var i = 0; i < fftSize; i++)
                {
                    tone[i] = 0.5 * Math.Sin((2.0 * Math.PI * toneHz * i / sampleRate) + 0.4)
                        + (1e-3 * (rng.NextDouble() - 0.5));
                }

                Assert.True(
                    PerformanceWowReference.IsSingleTone(Spectrum(tone, window), sampleRate),
                    $"tone {toneHz} Hz / {window}");
            }

            foreach (var sc in new[] { 16, 24, 72 })
            {
                foreach (var useRight in new[] { false, true })
                {
                    var carriers = useRight
                        ? PerformanceSignalGenerator.ResolveRightCarrierHz(sc)
                        : PerformanceSignalGenerator.ResolveLeftCarrierHz(sc);
                    var ofdm = new double[fftSize];
                    foreach (var hz in carriers)
                    {
                        var phase = rng.NextDouble() * 2.0 * Math.PI;
                        var amp = 0.05 * (0.5 + rng.NextDouble());
                        for (var i = 0; i < fftSize; i++)
                        {
                            ofdm[i] += amp * Math.Sin((2.0 * Math.PI * hz * i / sampleRate) + phase);
                        }
                    }

                    Assert.False(
                        PerformanceWowReference.IsSingleTone(Spectrum(ofdm, window), sampleRate),
                        $"SC-{sc} {(useRight ? "R" : "L")} / {window}");
                }
            }
        }
    }

    /// <summary>
    /// 受信ワーカーと同じ FFT（窓関数付き）でスペクトルを求めます。
    /// </summary>
    /// <param name="pcm">PCM。</param>
    /// <param name="window">窓関数。</param>
    /// <returns>FFT 結果。</returns>
    private static System.Numerics.Complex[] Spectrum(double[] pcm, PerformanceFftWindowKind window)
    {
        var bins = pcm.Select(v => new System.Numerics.Complex(v, 0)).ToArray();
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(bins, window);
        return bins;
    }

    /// <summary>
    /// 受信ワーカーと同じ Hann 窓 FFT＋放物線補間でピーク周波数を求めます。
    /// </summary>
    private static double ParabolicPeakHz(double[] pcm, int sampleRate)
    {
        var bins = pcm.Select(v => new System.Numerics.Complex(v, 0)).ToArray();
        PerformanceFftAnalyzer.ComputeSpectrumInPlace(bins, PerformanceFftWindowKind.Hanning);
        var best = 1;
        for (var bin = 1; bin < (bins.Length / 2) - 1; bin++)
        {
            if (bins[bin].Magnitude > bins[best].Magnitude)
            {
                best = bin;
            }
        }

        var delta = PerformanceWowReference.InterpolatePeakOffset(
            bins[best - 1].Magnitude,
            bins[best].Magnitude,
            bins[best + 1].Magnitude);
        return (best + delta) * sampleRate / (double)bins.Length;
    }
}
