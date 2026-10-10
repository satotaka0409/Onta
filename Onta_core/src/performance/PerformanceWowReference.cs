namespace Onta.Performance;

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
    /// 片側スペクトルの電力がピーク付近（±<see cref="SingleTonePeakHalfWidthHz"/>、最低 ±4 ビン）に
    /// <see cref="SingleTonePowerRatio"/> 以上集まっていれば単一トーンと判定します。
    /// OFDM は 16 本以上のキャリアに電力が分かれ、ピーク付近には 1〜3 本分（2 割以下）しか入らないため区別できます。
    /// </summary>
    /// <param name="bins">FFT 結果（全長。前半を片側スペクトルとして使う）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <returns>単一トーンのとき true。</returns>
    public static bool IsSingleTone(ReadOnlySpan<System.Numerics.Complex> bins, int sampleRate)
    {
        var half = bins.Length / 2;
        if (half < 8 || sampleRate <= 0)
        {
            return false;
        }

        var total = 0.0;
        var peakBin = 1;
        var peakPower = -1.0;
        for (var bin = 1; bin < half; bin++)
        {
            var power = PowerOf(bins[bin]);
            total += power;
            if (power > peakPower)
            {
                peakPower = power;
                peakBin = bin;
            }
        }

        if (total <= 1e-18)
        {
            return false;
        }

        var binHz = sampleRate / (double)bins.Length;
        var halfWidth = Math.Max(4, (int)Math.Ceiling(SingleTonePeakHalfWidthHz / binHz));
        var near = 0.0;
        for (var bin = Math.Max(1, peakBin - halfWidth); bin <= Math.Min(half - 1, peakBin + halfWidth); bin++)
        {
            near += PowerOf(bins[bin]);
        }

        return near >= total * SingleTonePowerRatio;
    }

    /// <summary><see cref="IsSingleTone"/> でピーク付近とみなす片側幅（Hz）。OFDM のキャリア間隔 223.9 Hz の半分弱。</summary>
    public const double SingleTonePeakHalfWidthHz = 100.0;

    /// <summary><see cref="IsSingleTone"/> で単一トーンとみなす、ピーク付近の電力の割合。</summary>
    public const double SingleTonePowerRatio = 0.6;

    /// <summary>
    /// 複素数の電力（振幅の 2 乗）を返します。
    /// </summary>
    /// <param name="value">複素数。</param>
    /// <returns>電力。</returns>
    private static double PowerOf(System.Numerics.Complex value) =>
        (value.Real * value.Real) + (value.Imaginary * value.Imaginary);

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

    /// <summary>
    /// 粗いピーク周波数の前後 ±<paramref name="halfWidthHz"/> で、Hann 窓を掛けた PCM の DTFT 振幅が最大になる周波数を黄金分割探索で求めます。
    /// FFT ビンの放物線補間は真の周波数がビン間のどこにあるかで最大ビン幅の約 5% ずれ、一定トーンでもワウ表示が 0 にならないため。
    /// </summary>
    /// <param name="pcm">FFT と同じ区間の PCM。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="coarseHz">FFT ピークから求めた粗い周波数（Hz）。</param>
    /// <param name="halfWidthHz">探索の片側幅（Hz）。FFT のビン幅を渡す（Hann のメインローブ内に収まる）。</param>
    /// <param name="windowed">作業領域（<paramref name="pcm"/> 以上の長さ）。</param>
    /// <returns>精密化したピーク周波数（Hz）。求められなければ <paramref name="coarseHz"/>。</returns>
    public static double RefinePeakFrequencyHz(
        ReadOnlySpan<double> pcm,
        int sampleRate,
        double coarseHz,
        double halfWidthHz,
        Span<double> windowed)
    {
        var n = pcm.Length;
        if (n < 16 || sampleRate <= 0 || coarseHz <= 1.0 || halfWidthHz <= 0 || windowed.Length < n)
        {
            return coarseHz;
        }

        var mean = 0.0;
        foreach (var sample in pcm)
        {
            mean += sample;
        }

        mean /= n;
        var step = 2.0 * Math.PI / (n - 1);
        for (var i = 0; i < n; i++)
        {
            windowed[i] = (pcm[i] - mean) * (0.5 - (0.5 * Math.Cos(step * i)));
        }

        var x = windowed[..n];
        var low = Math.Max(1.0, coarseHz - halfWidthHz);
        var high = Math.Min(sampleRate * 0.5, coarseHz + halfWidthHz);
        if (high <= low)
        {
            return coarseHz;
        }

        const double invPhi = 0.6180339887498949;
        var tolerance = coarseHz * 1e-7;
        var c1 = high - (invPhi * (high - low));
        var c2 = low + (invPhi * (high - low));
        var p1 = DtftPower(x, c1, sampleRate);
        var p2 = DtftPower(x, c2, sampleRate);
        for (var iteration = 0; iteration < 64 && high - low > tolerance; iteration++)
        {
            if (p1 < p2)
            {
                low = c1;
                c1 = c2;
                p1 = p2;
                c2 = low + (invPhi * (high - low));
                p2 = DtftPower(x, c2, sampleRate);
            }
            else
            {
                high = c2;
                c2 = c1;
                p2 = p1;
                c1 = high - (invPhi * (high - low));
                p1 = DtftPower(x, c1, sampleRate);
            }
        }

        return 0.5 * (low + high);
    }

    /// <summary>
    /// 窓掛け済み PCM の指定周波数における DTFT の電力を返します。
    /// </summary>
    /// <param name="windowed">窓掛け済み PCM。</param>
    /// <param name="frequencyHz">評価する周波数（Hz）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <returns>|Σ x[n]·e^(−j2πfn/fs)|²。</returns>
    private static double DtftPower(ReadOnlySpan<double> windowed, double frequencyHz, int sampleRate)
    {
        var (sin, cos) = Math.SinCos(2.0 * Math.PI * frequencyHz / sampleRate);
        double re = 0, im = 0, rotRe = 1, rotIm = 0;
        foreach (var sample in windowed)
        {
            re += sample * rotRe;
            im -= sample * rotIm;
            var nextRe = (rotRe * cos) - (rotIm * sin);
            rotIm = (rotRe * sin) + (rotIm * cos);
            rotRe = nextRe;
        }

        return (re * re) + (im * im);
    }

    /// <summary>
    /// 相対誤差 |measured/reference − 1| を返します。
    /// </summary>
    /// <param name="measuredHz">測定周波数（Hz）。</param>
    /// <param name="referenceHz">基準周波数（Hz）。</param>
    /// <returns>相対誤差（無次元）。</returns>
    private static double RelativeError(double measuredHz, double referenceHz) =>
        Math.Abs(measuredHz / referenceHz - 1.0);
}
