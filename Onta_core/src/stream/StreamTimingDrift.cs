using System.Numerics;

namespace Onta.Stream;

/// <summary>
/// 全シンボル共通値のパイロットがシンボルごとに回る位相から、シンボル位置のずれ（テープ速度の残差・ワウ）を推定します。
/// </summary>
/// <remarks>
/// 信号が 1 シンボルで δ サンプル遅れると、ビン k のパイロット位相は −2πkδ/N 回る。
/// 隣り合うシンボルのパイロット積の位相を、低いビンから順に展開しながら原点を通る直線で当てはめて δ を求める。
/// </remarks>
public static class StreamTimingDrift
{
    /// <summary>
    /// 等間隔に並ぶ OFDM シンボルの各パイロットの DFT 値を求めます。
    /// </summary>
    /// <param name="samples">PCM（実部のみ使用）。</param>
    /// <param name="length">有効サンプル数。</param>
    /// <param name="firstSymbolStart">最初のシンボル（CP 先頭）の位置。</param>
    /// <param name="symbolCount">測るシンボル数。</param>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <param name="cyclicPrefix">CP 長。</param>
    /// <param name="pilotBins">パイロットのビン番号。</param>
    /// <param name="destination">[シンボル, パイロット] の DFT 値の書き込み先。</param>
    /// <returns>測れたシンボル数（信号の末尾で打ち切り）。</returns>
    public static int MeasurePilots(
        Complex[] samples,
        int length,
        int firstSymbolStart,
        int symbolCount,
        int fftSize,
        int cyclicPrefix,
        IReadOnlyList<int> pilotBins,
        Complex[,] destination)
    {
        var cos = new double[fftSize];
        var sin = new double[fftSize];
        for (var n = 0; n < fftSize; n++)
        {
            var a = -2.0 * Math.PI * n / fftSize;
            cos[n] = Math.Cos(a);
            sin[n] = Math.Sin(a);
        }

        var symbolLength = fftSize + cyclicPrefix;
        for (var s = 0; s < symbolCount; s++)
        {
            // CP の中ほどから窓を始め、前後どちらへ数サンプルずれても隣のシンボルにかからないようにする
            var start = firstSymbolStart + (s * symbolLength) + (cyclicPrefix / 2);
            if (start < 0 || start + fftSize > length)
            {
                return s;
            }

            for (var i = 0; i < pilotBins.Count; i++)
            {
                var bin = pilotBins[i];
                double re = 0, im = 0;
                var index = 0;
                for (var n = 0; n < fftSize; n++)
                {
                    var x = samples[start + n].Real;
                    re += x * cos[index];
                    im += x * sin[index];
                    index += bin;
                    if (index >= fftSize)
                    {
                        index -= fftSize;
                    }
                }

                destination[s, i] = new Complex(re, im);
            }
        }

        return symbolCount;
    }

    /// <summary>
    /// パイロットごとの「後のシンボル × 前のシンボルの共役」から、1 シンボルあたりのずれ（サンプル）を求めます。
    /// </summary>
    /// <param name="products">パイロットごとの積（複数シンボル分を足したものでもよい）。</param>
    /// <param name="bins">各積のビン番号。</param>
    /// <param name="count">使う要素数。</param>
    /// <param name="fftSize">FFT サイズ。</param>
    /// <returns>1 シンボルあたりのずれ（正なら信号が後ろへずれていく）。求められなければ NaN。</returns>
    public static double SolveDrift(Complex[] products, int[] bins, int count, int fftSize)
    {
        var order = new int[count];
        for (var i = 0; i < count; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => bins[a].CompareTo(bins[b]));
        double sxy = 0, sxx = 0, slope = 0;
        foreach (var i in order)
        {
            var bin = bins[i];
            var w = products[i].Magnitude;
            if (bin <= 0 || w <= 0)
            {
                continue;
            }

            var theta = products[i].Phase;
            if (sxx > 0)
            {
                theta += 2.0 * Math.PI * Math.Round(((slope * bin) - theta) / (2.0 * Math.PI));
            }

            sxy += w * bin * theta;
            sxx += w * bin * bin;
            slope = sxy / sxx;
        }

        return sxx > 0 ? -slope * fftSize / (2.0 * Math.PI) : double.NaN;
    }
}
