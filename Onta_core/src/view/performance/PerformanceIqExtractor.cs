using System.Numerics;
using Onta.Core;

namespace Onta.View.Performance;

/// <summary>
/// 性能測定の I-Q コンスタレーション抽出です（OFDM FFT=256、パイロット等化）。
/// </summary>
internal static class PerformanceIqExtractor
{
    /// <summary>I-Q 用 FFT 長（OFDM 本体と同じ 256）。</summary>
    public const int FftSize = OfdmConfig.FixedFftSize;

    /// <summary>データ部 CP 長。</summary>
    public const int CyclicPrefixLength = 16;

    /// <summary>1 OFDM シンボルのサンプル数。</summary>
    public const int SymbolLength = FftSize + CyclicPrefixLength;

    /// <summary>CP 同期に使う直近サンプル数。</summary>
    public const int CaptureSamples = SymbolLength * 3;

    /// <summary>
    /// 直近 PCM から等化後データキャリアを取り出します。パイロットは含めません。
    /// </summary>
    /// <param name="pcm">振幅 PCM。</param>
    /// <param name="activeSubcarriers">サブキャリア数。</param>
    /// <param name="useRightCarriers">R 搬送波を使うか。</param>
    /// <param name="timeScratch">FFT 入力（長さ >= 256）。</param>
    /// <param name="fftScratch">FFT 作業（長さ 256）。</param>
    /// <param name="dest">等化後シンボル。</param>
    /// <param name="groups">グループ ID。</param>
    /// <returns>有効シンボル数。</returns>
    public static int ExtractEqualized(
        ReadOnlySpan<double> pcm,
        int activeSubcarriers,
        bool useRightCarriers,
        Complex[] timeScratch,
        Complex[] fftScratch,
        Span<Complex> dest,
        Span<byte> groups)
    {
        var sc = PerformanceSignalGenerator.ClampSubcarriers(activeSubcarriers);
        if (pcm.Length < FftSize || timeScratch.Length < FftSize || fftScratch.Length < FftSize)
        {
            return 0;
        }

        var dataStart = FindOfdmDataStart(pcm);
        if (dataStart < 0 || dataStart + FftSize > pcm.Length)
        {
            dataStart = pcm.Length - FftSize;
        }

        for (var i = 0; i < FftSize; i++)
        {
            timeScratch[i] = new Complex(pcm[dataStart + i], 0.0);
        }

        OfdmGenerator.ComputeRectangularForwardFftFromRealPcm(
            timeScratch.AsSpan(0, FftSize),
            fftScratch);

        var hz = useRightCarriers
            ? PerformanceSignalGenerator.ResolveRightCarrierHz(sc)
            : PerformanceSignalGenerator.ResolveLeftCarrierHz(sc);
        var bins = new int[hz.Length];
        var used = new HashSet<int>();
        var maxBin = (FftSize / 2) - 1;
        for (var i = 0; i < hz.Length; i++)
        {
            bins[i] = AllocateUniqueBin(
                OfdmConfig.HzToPositiveBin(hz[i], FftSize, PerformanceSignalGenerator.SampleRate),
                used,
                maxBin);
        }

        var written = 0;
        var destLen = Math.Min(dest.Length, groups.Length);
        for (var start = 0; start + 7 < bins.Length && written < destLen; start += 8)
        {
            var p2 = fftScratch[bins[start + 2]];
            var p6 = fftScratch[bins[start + 6]];
            var group = (byte)Math.Clamp(start / 8, 0, 7);
            for (var ch = 0; ch < 8 && written < destLen; ch++)
            {
                if (ch is 2 or 6)
                {
                    continue;
                }

                var pilot = ch < 4 ? p2 : p6;
                if (pilot.Magnitude < 1e-9)
                {
                    continue;
                }

                dest[written] = fftScratch[bins[start + ch]] / pilot;
                groups[written] = group;
                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// CP 相関が最大になる OFDM データ開始位置を探します。
    /// </summary>
    public static int FindOfdmDataStart(ReadOnlySpan<double> pcm)
    {
        if (pcm.Length < SymbolLength)
        {
            return Math.Max(0, pcm.Length - FftSize);
        }

        var searchFrom = Math.Max(0, pcm.Length - (SymbolLength * 2));
        var searchTo = pcm.Length - SymbolLength;
        var best = searchFrom;
        var bestScore = double.NegativeInfinity;
        for (var t = searchFrom; t <= searchTo; t++)
        {
            var corr = 0.0;
            var e1 = 0.0;
            var e2 = 0.0;
            for (var i = 0; i < CyclicPrefixLength; i++)
            {
                var a = pcm[t + i];
                var b = pcm[t + FftSize + i];
                corr += a * b;
                e1 += a * a;
                e2 += b * b;
            }

            var denom = Math.Sqrt(e1 * e2);
            var score = denom > 1e-12 ? corr / denom : 0.0;
            if (score > bestScore)
            {
                bestScore = score;
                best = t;
            }
        }

        return best + CyclicPrefixLength;
    }

    /// <summary>
    /// 正周波数ビンを重複なく割り当てます。
    /// </summary>
    private static int AllocateUniqueBin(int preferred, HashSet<int> used, int maxBin)
    {
        var bin = Math.Clamp(preferred, 1, maxBin);
        while (used.Contains(bin))
        {
            bin++;
            if (bin > maxBin)
            {
                bin = preferred - 1;
                while (bin >= 1 && used.Contains(bin))
                {
                    bin--;
                }

                if (bin < 1)
                {
                    bin = 1;
                    break;
                }

                break;
            }
        }

        used.Add(bin);
        return bin;
    }
}
