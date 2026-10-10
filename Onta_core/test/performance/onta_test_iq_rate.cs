using System.Numerics;
using Onta.Performance;
using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.Performance;

/// <summary>
/// 性能測定の受信 I-Q を、デバイスレートから変調クロックへ戻して等化できることです。
/// </summary>
public sealed class OntaTestIqRate
{
    [Fact]
    public void Extract_44100Ofdm_LandsInsideAxis()
    {
        var pcm = Synthesize(44100);
        var count = Extract(pcm, 44100, align: false);

        Assert.InRange(count, 6, 64);
        AssertInsideAxis(count);
    }

    [Theory]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Extract_DeviceRateCaptureOf44100Ofdm_LandsInsideAxis(int captureRate)
    {
        var native = Synthesize(44100);
        var captured = Resample(native, 44100, captureRate);
        var count = Extract(captured, captureRate, align: true);

        Assert.InRange(count, 6, 64);
        AssertInsideAxis(count);
    }

    /// <summary>
    /// 72SC（Group I まで）のキャリア周波数を引け、L/R の I-Q を抽出できること（SC ごとのキャッシュの範囲外アクセス防止）。
    /// </summary>
    [Fact]
    public void Extract_Sc72Stereo_ResolvesCarriersAndGroupI()
    {
        var leftHz = PerformanceSignalGenerator.ResolveLeftCarrierHz(72);
        var rightHz = PerformanceSignalGenerator.ResolveRightCarrierHz(72);
        Assert.Equal(72, leftHz.Length);
        Assert.Equal(72, rightHz.Length);

        var (left, right) = PerformanceSignalGenerator.GenerateModulated(
            72,
            ModulationScheme.Qam16,
            ChannelMode.Stereo,
            durationSeconds: 0.08,
            amplitude: 0.5,
            sampleRate: 44100);
        var time = new Complex[PerformanceIqExtractor.FftSize];
        var fft = new Complex[PerformanceIqExtractor.FftSize];
        var points = new Complex[128];
        var groups = new byte[128];
        var leftCount = PerformanceIqExtractor.ExtractEqualized(
            ToReal(left), 72, useRightCarriers: false, time, fft, points, groups, sampleRate: 44100);
        var rightCount = PerformanceIqExtractor.ExtractEqualized(
            ToReal(right), 72, useRightCarriers: true, time, fft, points.AsSpan(leftCount), groups.AsSpan(leftCount), sampleRate: 44100);

        Assert.Equal(54, leftCount);
        Assert.Equal(54, rightCount);
        Assert.Contains((byte)8, groups.AsSpan(0, leftCount + rightCount).ToArray());
    }

    /// <summary>
    /// 複素 PCM の実部を取り出します。
    /// </summary>
    /// <param name="pcm">複素 PCM。</param>
    /// <returns>実数 PCM。</returns>
    private static double[] ToReal(Complex[] pcm)
    {
        var real = new double[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            real[i] = pcm[i].Real;
        }

        return real;
    }

    /// <summary>
    /// 44100 Hz の OFDM PCM を生成します。
    /// </summary>
    /// <param name="sampleRate">生成サンプリング周波数。</param>
    /// <returns>実数 PCM。</returns>
    private static double[] Synthesize(int sampleRate)
    {
        var (left, _) = PerformanceSignalGenerator.GenerateModulated(
            16,
            ModulationScheme.Qpsk,
            ChannelMode.Mono,
            durationSeconds: 0.08,
            amplitude: 0.5,
            sampleRate: sampleRate);
        var pcm = new double[left.Length];
        for (var i = 0; i < left.Length; i++)
        {
            pcm[i] = left[i].Real;
        }

        return pcm;
    }

    /// <summary>
    /// PCM を別レートへ変換します。
    /// </summary>
    /// <param name="pcm">入力 PCM。</param>
    /// <param name="sourceRate">入力サンプリング周波数。</param>
    /// <param name="targetRate">出力サンプリング周波数。</param>
    /// <returns>変換後 PCM。</returns>
    private static double[] Resample(double[] pcm, int sourceRate, int targetRate)
    {
        var input = new float[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
        {
            input[i] = (float)pcm[i];
        }

        var output = new List<float>();
        new StreamingPcmResampler(sourceRate, targetRate, channels: 1).Process(input, output);
        var aligned = new double[output.Count];
        for (var i = 0; i < output.Count; i++)
        {
            aligned[i] = output[i];
        }

        return aligned;
    }

    /// <summary>
    /// 等化後シンボル数を返します。点は <paramref name="scratch"/> に残します。
    /// </summary>
    /// <param name="pcm">入力 PCM。</param>
    /// <param name="sampleRate">キャプチャのサンプリング周波数。</param>
    /// <param name="align">変調クロックへ戻してから等化するか。</param>
    /// <returns>有効シンボル数。</returns>
    private static int Extract(double[] pcm, int sampleRate, bool align)
    {
        _dest = new Complex[64];
        var groups = new byte[64];
        var time = new Complex[PerformanceIqExtractor.FftSize];
        var fft = new Complex[PerformanceIqExtractor.FftSize];
        if (align)
        {
            return PerformanceIqExtractor.ExtractEqualizedAtSynthesisRate(
                pcm,
                sampleRate,
                activeSubcarriers: 16,
                useRightCarriers: false,
                time,
                fft,
                _dest,
                groups);
        }

        return PerformanceIqExtractor.ExtractEqualized(
            pcm,
            activeSubcarriers: 16,
            useRightCarriers: false,
            time,
            fft,
            _dest,
            groups,
            sampleRate: sampleRate);
    }

    private static Complex[] _dest = [];

    /// <summary>
    /// 等化点が QPSK の描画軸の内側（半径 1.5 未満）にあることを確認します。
    /// </summary>
    /// <param name="count">確認する点数。</param>
    private static void AssertInsideAxis(int count)
    {
        var bad = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var z = _dest[i];
            if (!double.IsFinite(z.Real) || !double.IsFinite(z.Imaginary) || z.Magnitude >= 1.5)
            {
                bad.Add($"[{i}]={z.Magnitude:0.00}");
            }
        }

        Assert.True(bad.Count == 0, string.Join(" ", bad));
    }
}
