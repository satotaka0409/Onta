using System.Numerics;
using Onta.Core;

namespace Onta.View.Performance;

/// <summary>
/// 性能測定 FFT の窓関数です。
/// </summary>
internal enum PerformanceFftWindowKind : byte
{
    /// <summary>Hanning（Hann）。</summary>
    Hanning = 0,

    /// <summary>Hamming。</summary>
    Hamming = 1,

    /// <summary>Blackman。</summary>
    Blackman = 2,

    /// <summary>Flat Top。</summary>
    FlatTop = 3,

    /// <summary>矩形窓（窓なし）。</summary>
    Rectangular = 4
}

/// <summary>
/// 性能測定表示用 FFT（可変長・窓関数付き）です。
/// </summary>
internal static class PerformanceFftAnalyzer
{
    /// <summary>選択可能な FFT 長。</summary>
    public static readonly int[] SupportedSizes = [1024, 2048, 4096];

    /// <summary>既定 FFT 長。</summary>
    public const int DefaultSize = 2048;

    /// <summary>最大 FFT 長（作業バッファ確保用）。</summary>
    public const int MaxSize = 4096;

    /// <summary>窓種数（キャッシュ行）。</summary>
    private const int WindowKindCount = 5;

    /// <summary>対応 FFT 長の種類数（1024/2048/4096）。</summary>
    private const int SizeCount = 3;

    /// <summary>コヒーレントゲイン込みの窓テーブル（kind * SizeCount + sizeIndex）。</summary>
    private static readonly double[]?[] WindowTables = new double[WindowKindCount * SizeCount][];

    private static readonly object WindowLock = new();

    /// <summary>
    /// FFT 長を対応値へ丸めます。
    /// </summary>
    /// <param name="fftSize">希望 FFT 長。</param>
    /// <returns>1024 / 2048 / 4096。</returns>
    public static int ClampSize(int fftSize) =>
        fftSize switch
        {
            <= 1024 => 1024,
            <= 2048 => 2048,
            _ => 4096
        };

    /// <summary>
    /// 窓種を定義済み値へ丸めます。
    /// </summary>
    /// <param name="kind">窓種。</param>
    /// <returns>有効な窓種。</returns>
    public static PerformanceFftWindowKind ClampWindow(PerformanceFftWindowKind kind) =>
        Enum.IsDefined(typeof(PerformanceFftWindowKind), kind)
            ? kind
            : PerformanceFftWindowKind.Hanning;

    /// <summary>
    /// 実数 PCM（Imag=0）が既に入ったバッファへ窓を掛けて FFT します（破壊的）。
    /// <paramref name="buffer"/> の長さが FFT 長です。
    /// </summary>
    /// <param name="buffer">PCM 実部 → スペクトル。</param>
    /// <param name="windowKind">窓関数。</param>
    public static void ComputeSpectrumInPlace(Complex[] buffer, PerformanceFftWindowKind windowKind)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (buffer.Length == 0)
        {
            throw new ArgumentException("Buffer is empty.", nameof(buffer));
        }

        var kind = ClampWindow(windowKind);
        if (kind != PerformanceFftWindowKind.Rectangular)
        {
            ApplyCachedWindowInPlace(buffer, kind);
        }

        OfdmGenerator.ComputeRectangularForwardFftFromRealPcm(buffer, buffer);
    }

    /// <summary>
    /// キャッシュ済み窓（ゲイン補正込み）を 1 パスで掛けます。
    /// </summary>
    /// <param name="buffer">実部に PCM が入ったバッファ（破壊的）。</param>
    /// <param name="windowKind">窓種。</param>
    private static void ApplyCachedWindowInPlace(Complex[] buffer, PerformanceFftWindowKind windowKind)
    {
        var n = buffer.Length;
        var table = GetOrCreateWindowTable(n, windowKind);
        for (var i = 0; i < n; i++)
        {
            buffer[i] = new Complex(buffer[i].Real * table[i], 0.0);
        }
    }

    /// <summary>
    /// 窓テーブルを取得（なければ生成）します。
    /// </summary>
    /// <param name="n">FFT 長。</param>
    /// <param name="windowKind">窓種。</param>
    /// <returns>コヒーレントゲイン補正済み窓係数。</returns>
    private static double[] GetOrCreateWindowTable(int n, PerformanceFftWindowKind windowKind)
    {
        var slot = ((int)windowKind * SizeCount) + SizeToIndex(n);
        var existing = Volatile.Read(ref WindowTables[slot]);
        if (existing is not null && existing.Length == n)
        {
            return existing;
        }

        lock (WindowLock)
        {
            existing = WindowTables[slot];
            if (existing is not null && existing.Length == n)
            {
                return existing;
            }

            var created = BuildWindowTable(n, windowKind);
            Volatile.Write(ref WindowTables[slot], created);
            return created;
        }
    }

    /// <summary>
    /// FFT 長をキャッシュ列インデックスへ変換します。
    /// </summary>
    /// <param name="n">FFT 長（1024/2048/4096）。</param>
    /// <returns>0..2 のスロット。</returns>
    private static int SizeToIndex(int n) =>
        n switch
        {
            1024 => 0,
            2048 => 1,
            _ => 2
        };

    /// <summary>
    /// コヒーレントゲイン補正済みの窓係数を生成します。
    /// </summary>
    /// <param name="n">FFT 長。</param>
    /// <param name="windowKind">窓種。</param>
    /// <returns>長さ n の窓係数。</returns>
    private static double[] BuildWindowTable(int n, PerformanceFftWindowKind windowKind)
    {
        var table = new double[n];
        var denom = Math.Max(1, n - 1);
        var twoPi = 2.0 * Math.PI;
        var sumW = 0.0;
        for (var i = 0; i < n; i++)
        {
            var w = WindowSample(windowKind, i, denom, twoPi);
            table[i] = w;
            sumW += w;
        }

        var coherent = sumW / n;
        if (coherent > 1e-12)
        {
            var inv = 1.0 / coherent;
            for (var i = 0; i < n; i++)
            {
                table[i] *= inv;
            }
        }

        return table;
    }

    /// <summary>
    /// 1 サンプル分の窓係数を返します。
    /// </summary>
    /// <param name="kind">窓種。</param>
    /// <param name="i">サンプル番号。</param>
    /// <param name="denom">正規化分母（通常 n-1）。</param>
    /// <param name="twoPi">2π。</param>
    /// <returns>窓係数。</returns>
    private static double WindowSample(
        PerformanceFftWindowKind kind,
        int i,
        int denom,
        double twoPi)
    {
        var x = twoPi * i / denom;
        return kind switch
        {
            PerformanceFftWindowKind.Hamming => 0.54 - (0.46 * Math.Cos(x)),
            PerformanceFftWindowKind.Blackman =>
                0.42 - (0.5 * Math.Cos(x)) + (0.08 * Math.Cos(2.0 * x)),
            PerformanceFftWindowKind.FlatTop =>
                0.21557895
                - (0.41663158 * Math.Cos(x))
                + (0.277263158 * Math.Cos(2.0 * x))
                - (0.083578947 * Math.Cos(3.0 * x))
                + (0.006947368 * Math.Cos(4.0 * x)),
            PerformanceFftWindowKind.Rectangular => 1.0,
            // Hanning: 既存可視化と同じ 1-cos（標準 Hann の 2 倍）
            _ => 1.0 - Math.Cos(x)
        };
    }
}
