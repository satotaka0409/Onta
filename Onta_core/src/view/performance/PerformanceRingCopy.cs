using System.Numerics;

namespace Onta.View.Performance;

/// <summary>
/// リングバッファ末尾の高速コピー（剰余をサンプルごと計算しない）です。
/// </summary>
internal static class PerformanceRingCopy
{
    /// <summary>
    /// リング末尾 <paramref name="count"/> サンプルを線形バッファへコピーします。
    /// </summary>
    /// <param name="ring">リング。</param>
    /// <param name="writeTotal">累積書き込み数。</param>
    /// <param name="dest">出力。</param>
    /// <param name="count">コピー数。</param>
    public static void CopyTail(double[] ring, long writeTotal, Span<double> dest, int count)
    {
        if (count <= 0)
        {
            return;
        }

        var cap = ring.Length;
        var start = (int)((writeTotal - count) % cap);
        if (start < 0)
        {
            start += cap;
        }

        var first = Math.Min(count, cap - start);
        ring.AsSpan(start, first).CopyTo(dest);
        if (first < count)
        {
            ring.AsSpan(0, count - first).CopyTo(dest[first..]);
        }
    }

    /// <summary>
    /// リング末尾を Complex（Imag=0）の FFT 窓へ詰めます。
    /// </summary>
    /// <param name="ring">リング。</param>
    /// <param name="writeTotal">累積書き込み数。</param>
    /// <param name="destination">FFT 入力。</param>
    /// <param name="fftSize">窓長。</param>
    public static void FillComplexWindow(double[] ring, long writeTotal, Complex[] destination, int fftSize)
    {
        var cap = ring.Length;
        var start = (int)((writeTotal - fftSize) % cap);
        if (start < 0)
        {
            start += cap;
        }

        var first = Math.Min(fftSize, cap - start);
        for (var i = 0; i < first; i++)
        {
            destination[i] = new Complex(ring[start + i], 0.0);
        }

        for (var i = first; i < fftSize; i++)
        {
            destination[i] = new Complex(ring[i - first], 0.0);
        }
    }

    /// <summary>
    /// 左右リング末尾を同時に線形バッファへコピーします。
    /// </summary>
    /// <param name="leftRing">L リング。</param>
    /// <param name="rightRing">R リング。</param>
    /// <param name="writeTotal">累積書き込み数。</param>
    /// <param name="left">L 出力。</param>
    /// <param name="right">R 出力。</param>
    /// <param name="count">コピー数。</param>
    /// <param name="copyRightFromLeft">R を L の複製にするか。</param>
    public static void CopyStereoTail(
        double[] leftRing,
        double[] rightRing,
        long writeTotal,
        Span<double> left,
        Span<double> right,
        int count,
        bool copyRightFromLeft)
    {
        if (count <= 0)
        {
            return;
        }

        var cap = leftRing.Length;
        var start = (int)((writeTotal - count) % cap);
        if (start < 0)
        {
            start += cap;
        }

        var first = Math.Min(count, cap - start);
        leftRing.AsSpan(start, first).CopyTo(left);
        if (first < count)
        {
            leftRing.AsSpan(0, count - first).CopyTo(left[first..]);
        }

        if (copyRightFromLeft)
        {
            left[..count].CopyTo(right);
            return;
        }

        rightRing.AsSpan(start, first).CopyTo(right);
        if (first < count)
        {
            rightRing.AsSpan(0, count - first).CopyTo(right[first..]);
        }
    }
}
