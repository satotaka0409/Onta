using System.Numerics;
using Onta.Core;

namespace Onta.View.Performance;

/// <summary>
/// リサージュ画面用の周波数カウンタ／歪み率（THD）計測です。
/// </summary>
internal static class LissajousMeterAnalyzer
{
    /// <summary>解析 FFT 長（Hann テーブルあり）。</summary>
    public const int FftSize = PerformanceConstants.VizFftSize;

    /// <summary>
    /// 1 チャネル分の計測結果です。
    /// </summary>
    /// <param name="FrequencyHz">周波数（1 Hz 単位）。無効時は -1。</param>
    /// <param name="ThdPercent">歪み率 THD（%）。無効時は負。</param>
    public readonly record struct ChannelMeters(int FrequencyHz, double ThdPercent);

    /// <summary>
    /// PCM から周波数（1 Hz）と歪み率（0.01 % 分解能）を求めます。
    /// </summary>
    /// <param name="pcm">実数 PCM（-1..1）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="timeScratch">長さ <see cref="FftSize"/> 以上の作業用（実部に PCM）。</param>
    /// <param name="fftScratch">長さ <see cref="FftSize"/> の FFT 作業用。</param>
    public static ChannelMeters Analyze(
        ReadOnlySpan<double> pcm,
        int sampleRate,
        Complex[] timeScratch,
        Complex[] fftScratch)
    {
        if (pcm.Length < 64 || sampleRate <= 0
            || timeScratch.Length < FftSize || fftScratch.Length < FftSize)
        {
            return new ChannelMeters(-1, -1);
        }

        var freqHz = EstimateFrequencyHz(pcm, sampleRate);
        FillTimeWindow(pcm, timeScratch.AsSpan(0, FftSize));
        OfdmGenerator.ComputeForwardSpectrumFromRealPcm(timeScratch.AsSpan(0, FftSize), fftScratch);

        if (freqHz < 1.0)
        {
            freqHz = FindPeakFrequencyHz(fftScratch, sampleRate);
        }

        if (freqHz < 1.0)
        {
            return new ChannelMeters(-1, -1);
        }

        var freqInt = (int)Math.Round(freqHz);
        if (freqInt < 1)
        {
            return new ChannelMeters(-1, -1);
        }

        var thd = ComputeThdPercent(fftScratch, sampleRate, freqHz);
        // 表示は 1/100 % まで（丸め）
        thd = Math.Round(thd, 2, MidpointRounding.AwayFromZero);
        return new ChannelMeters(freqInt, thd);
    }

    /// <summary>
    /// ゼロクロス間隔から周波数を推定します（正弦波カウンタ向け）。
    /// </summary>
    private static double EstimateFrequencyHz(ReadOnlySpan<double> pcm, int sampleRate)
    {
        // DC 除去
        double sum = 0;
        for (var i = 0; i < pcm.Length; i++)
        {
            sum += pcm[i];
        }

        var mean = sum / pcm.Length;
        var prev = pcm[0] - mean;
        var periods = new List<double>(64);
        for (var i = 1; i < pcm.Length; i++)
        {
            var cur = pcm[i] - mean;
            // 上昇ゼロクロス
            if (prev < 0.0 && cur >= 0.0)
            {
                var frac = prev / (prev - cur); // 0..1（サンプル間位置）
                var cross = (i - 1) + frac;
                if (periods.Count > 0)
                {
                    // periods にはクロス時刻を貯め、差分が周期
                }

                periods.Add(cross);
            }

            prev = cur;
        }

        if (periods.Count < 3)
        {
            return 0;
        }

        double periodSum = 0;
        var periodCount = 0;
        for (var i = 1; i < periods.Count; i++)
        {
            var p = periods[i] - periods[i - 1];
            if (p > 2 && p < sampleRate) // 1 Hz〜 Nyquist 付近
            {
                periodSum += p;
                periodCount++;
            }
        }

        if (periodCount == 0)
        {
            return 0;
        }

        var meanPeriod = periodSum / periodCount;
        return sampleRate / meanPeriod;
    }

    /// <summary>
    /// 末尾 FFT 窓を Complex（Imag=0）へ詰めます。
    /// </summary>
    private static void FillTimeWindow(ReadOnlySpan<double> pcm, Span<Complex> destination)
    {
        var n = destination.Length;
        var offset = Math.Max(0, pcm.Length - n);
        for (var i = 0; i < n; i++)
        {
            var src = offset + i < pcm.Length ? pcm[offset + i] : 0.0;
            destination[i] = new Complex(src, 0);
        }
    }

    /// <summary>
    /// FFT ピークビン（放物線補間）から周波数を求めます。
    /// </summary>
    private static double FindPeakFrequencyHz(Complex[] bins, int sampleRate)
    {
        var half = bins.Length / 2;
        var bestMag = -1.0;
        var bestBin = 1;
        var last = half - 1;
        for (var bin = 1; bin < last; bin++)
        {
            var mag = bins[bin].Magnitude;
            if (mag > bestMag)
            {
                bestMag = mag;
                bestBin = bin;
            }
        }

        if (bestMag < 1e-9)
        {
            return 0;
        }

        var leftMag = bins[bestBin - 1].Magnitude;
        var rightMag = bins[bestBin + 1].Magnitude;
        var delta = PerformanceWowReference.InterpolatePeakOffset(leftMag, bestMag, rightMag);
        return (bestBin + delta) * (sampleRate / (double)bins.Length);
    }

    /// <summary>
    /// THD(%) = sqrt(Σ|H_k|^2) / |H1| × 100（2〜10 次、Nyquist 未満）。
    /// </summary>
    private static double ComputeThdPercent(Complex[] bins, int sampleRate, double fundamentalHz)
    {
        if (fundamentalHz < 1.0)
        {
            return 0;
        }

        var fundMag = InterpolatedMagnitude(bins, sampleRate, fundamentalHz);
        if (fundMag < 1e-9)
        {
            return 0;
        }

        double harmPower = 0;
        var maxHarm = Math.Min(10, (int)Math.Floor((sampleRate * 0.49) / fundamentalHz));
        for (var k = 2; k <= maxHarm; k++)
        {
            var mag = InterpolatedMagnitude(bins, sampleRate, fundamentalHz * k);
            harmPower += mag * mag;
        }

        if (harmPower <= 0)
        {
            return 0;
        }

        return Math.Sqrt(harmPower) / fundMag * 100.0;
    }

    /// <summary>
    /// 任意周波数の振幅を隣接ビン線形補間で求めます。
    /// </summary>
    private static double InterpolatedMagnitude(Complex[] bins, int sampleRate, double hz)
    {
        var binExact = hz * bins.Length / sampleRate;
        var half = bins.Length / 2;
        if (binExact < 1 || binExact >= half - 1)
        {
            return 0;
        }

        var i0 = (int)Math.Floor(binExact);
        var frac = binExact - i0;
        var m0 = bins[i0].Magnitude;
        var m1 = bins[i0 + 1].Magnitude;
        return (m0 * (1.0 - frac)) + (m1 * frac);
    }
}
