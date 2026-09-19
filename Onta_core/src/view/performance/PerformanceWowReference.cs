namespace Onta.View.Performance;

/// <summary>
/// 性能測定ワウの基準周波数です。送信側に存在する周波数の最近傍へロックします。
/// </summary>
public static class PerformanceWowReference
{
    /// <summary>無変調トーンの送信周波数（Hz）。</summary>
    public static readonly double[] ToneFrequenciesHz =
        [315, 400, 1000, 3000, 8000, 10000, 12500, 15000, 20000];

    /// <summary>初期ロックを許す相対誤差。</summary>
    public const double LockRelativeError = 0.05;

    /// <summary>ロック維持を切る相対誤差（別トーンへ切り替わった判定）。</summary>
    public const double RelockRelativeError = 0.08;

    /// <summary>
    /// 信号モードに応じた送信側周波数候補を返します。スイープは空です。
    /// </summary>
    /// <param name="mode">性能測定の信号モード。</param>
    /// <param name="activeSubcarriers">OFDM のサブキャリア数。</param>
    /// <param name="useRightCarriers">R 搬送波を使うか。</param>
    /// <returns>基準候補（Hz）。</returns>
    internal static double[] ResolveCandidates(
        PerformanceSignalMode mode,
        int activeSubcarriers,
        bool useRightCarriers)
    {
        return mode switch
        {
            PerformanceSignalMode.Tone => ToneFrequenciesHz,
            PerformanceSignalMode.Modulated => useRightCarriers
                ? PerformanceSignalGenerator.ResolveRightCarrierHz(activeSubcarriers)
                : PerformanceSignalGenerator.ResolveLeftCarrierHz(activeSubcarriers),
            _ => []
        };
    }

    /// <summary>
    /// 測定周波数に最も近い候補を返します。候補が無ければ 0 です。
    /// </summary>
    /// <param name="measuredHz">FFT で得たピーク周波数（Hz）。</param>
    /// <param name="candidates">送信側周波数候補。</param>
    /// <returns>最近傍の基準周波数（Hz）。</returns>
    public static double FindNearest(double measuredHz, ReadOnlySpan<double> candidates)
    {
        if (measuredHz <= 1.0 || candidates.IsEmpty)
        {
            return 0;
        }

        var best = 0.0;
        var bestErr = double.PositiveInfinity;
        foreach (var hz in candidates)
        {
            if (hz <= 1.0)
            {
                continue;
            }

            var err = Math.Abs(measuredHz - hz);
            if (err < bestErr)
            {
                bestErr = err;
                best = hz;
            }
        }

        return best;
    }

    /// <summary>
    /// ピークを送信側周波数へロックし、別周波数へ跳ねたら張り直します。
    /// </summary>
    /// <param name="measuredHz">測定ピーク（Hz）。</param>
    /// <param name="candidates">送信側周波数候補。</param>
    /// <param name="lockedHz">現在のロック基準。未ロックは 0。</param>
    /// <returns>ロックできたか。</returns>
    public static bool TryLock(double measuredHz, ReadOnlySpan<double> candidates, ref double lockedHz)
    {
        var nearest = FindNearest(measuredHz, candidates);
        if (nearest <= 1.0 || measuredHz <= 1.0)
        {
            return lockedHz > 1.0;
        }

        if (lockedHz <= 1.0)
        {
            if (RelativeError(measuredHz, nearest) > LockRelativeError)
            {
                return false;
            }

            lockedHz = nearest;
            return true;
        }

        if (RelativeError(measuredHz, lockedHz) > RelockRelativeError
            && RelativeError(measuredHz, nearest) <= LockRelativeError)
        {
            lockedHz = nearest;
        }

        return true;
    }

    /// <summary>
    /// 基準に対するワウ量（%）を返します。
    /// </summary>
    /// <param name="measuredHz">測定ピーク（Hz）。</param>
    /// <param name="referenceHz">ロックした送信周波数（Hz）。</param>
    /// <returns>ワウ（%）。</returns>
    public static double ToWowPercent(double measuredHz, double referenceHz)
    {
        if (referenceHz <= 1.0 || measuredHz <= 1.0)
        {
            return 0;
        }

        return Math.Clamp((measuredHz / referenceHz - 1.0) * 100.0, -1.5, 1.5);
    }

    /// <summary>
    /// 隣接ビンから放物線補間したピークビン位置を返します。
    /// </summary>
    /// <param name="leftMag">ピーク−1 ビンの振幅。</param>
    /// <param name="peakMag">ピークビンの振幅。</param>
    /// <param name="rightMag">ピーク＋1 ビンの振幅。</param>
    /// <returns>ピークビンからのオフセット（−0.5〜+0.5）。</returns>
    public static double InterpolatePeakOffset(double leftMag, double peakMag, double rightMag)
    {
        var denom = leftMag - (2.0 * peakMag) + rightMag;
        if (Math.Abs(denom) < 1e-18)
        {
            return 0;
        }

        return Math.Clamp(0.5 * (leftMag - rightMag) / denom, -0.5, 0.5);
    }

    private static double RelativeError(double measuredHz, double referenceHz) =>
        Math.Abs(measuredHz / referenceHz - 1.0);
}
