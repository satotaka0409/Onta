using System.Numerics;

namespace Onta.Core;

/// <summary>
/// ライブ入力から FH 手前の無変調区間（ヘッダー OFDM シンボル周期で同一波形が繰り返す区間）を逐次探し、
/// その終端＝FH 変調部の先頭位置を求める検出器です。
/// </summary>
/// <remarks>
/// 無変調区間は 1 シンボル（CP 込み）周期で完全に繰り返すため、その周期ラグの自己相関がほぼ 1 になる。
/// 変調部はシンボルごとに内容が変わるため CP 分しか相関せず、無音・雑音も相関しない。
/// </remarks>
internal sealed class PreambleAnchorDetector
{
    /// <summary>無変調区間とみなす正規化自己相関の下限。</summary>
    private const double CorrelationThreshold = 0.7;

    /// <summary>無音とみなさない窓内平均電力の下限。</summary>
    private const double MinMeanPower = 1e-6;

    /// <summary>テープ速度ずれを吸収するラグの探索幅（周期比。±4% まで）。</summary>
    private const double LagToleranceRatio = 0.04;

    /// <summary>窓の電力のうちキャリアビンが占める割合の下限（単一トーン・雑音・音楽を除外）。</summary>
    private const double MinCarrierFraction = 0.6;

    /// <summary>パイロットと両隣キャリアに要求する、キャリア平均電力に対する比の下限。</summary>
    private const double MinNeighborPowerRatio = 0.05;

    /// <summary>パイロット位相と両隣からの補間位相の差（平均絶対値）の上限。QPSK 点との差 45° を切り分ける。</summary>
    private const double MaxPilotPhaseResidual = 20.0 * Math.PI / 180.0;

    /// <summary>シンボル位相（終端補正）に使う時間波形相関の絶対値の下限。</summary>
    private const double SymbolPhaseMinCorrelation = 0.5;

    private readonly Complex[][] _referenceSymbols;
    private readonly int[][] _referenceBins;
    private readonly int _fftSize;
    private readonly double[] _cosTable;
    private readonly double[] _sinTable;
    private readonly int _tailUnmodulatedSamples;
    private readonly int _period;
    private readonly int _window;
    private readonly int _hop;
    private readonly int _lagTolerance;
    private readonly int _minRunSamples;
    private readonly int _maxGapSamples;

    private int _scanPos;
    private int _runStart = -1;
    private int _lastGood = -1;
    private int _lastGoodLag;

    /// <summary>
    /// 直近に検出したアンカー直前の無変調区間で測った繰り返し周期（サンプル、小数）。
    /// 公称周期との比がテープ速度のずれになります（周期が短いほどテープが速い）。
    /// </summary>
    public double LastPeriod { get; private set; }

    /// <summary>
    /// 公称の繰り返し周期（ヘッダー OFDM 1 シンボル、CP 込み）です。
    /// </summary>
    public int NominalPeriod => _period;

    /// <summary>
    /// 直近に検出したアンカーで、信号の極性が反転して届いていた場合 true です。
    /// </summary>
    public bool LastPolarityInverted { get; private set; }

    /// <summary>
    /// 検出器を初期化します。
    /// </summary>
    /// <param name="symbolSamples">無変調区間の繰り返し周期（ヘッダー OFDM 1 シンボル、CP 込み）。</param>
    /// <param name="fftSize">ヘッダー OFDM の FFT 長（シンボルから CP を除いた長さ）。</param>
    /// <param name="minRunSamples">アンカーとみなす無変調区間の最短長。BH（0.3 秒）を除外する長さを指定します。</param>
    /// <param name="referenceSymbols">無変調 1 シンボルの理想波形（グリッド候補ごと）。空なら照合しません。</param>
    /// <param name="tailUnmodulatedSamples">FH 直前の無変調区間の公称長。0 ならシンボル位相での終端補正をしません。</param>
    /// <remarks>ゼロ詰めの多いヘッダー変調部も同一シンボルが続いて周期的に見えるため、理想波形との照合で除外する。</remarks>
    public PreambleAnchorDetector(
        int symbolSamples,
        int fftSize,
        int minRunSamples,
        Complex[][]? referenceSymbols = null,
        int tailUnmodulatedSamples = 0)
    {
        _tailUnmodulatedSamples = Math.Max(0, tailUnmodulatedSamples);
        _period = Math.Max(16, symbolSamples);
        _fftSize = Math.Clamp(fftSize, 8, _period);
        _cosTable = new double[_fftSize];
        _sinTable = new double[_fftSize];
        for (var k = 0; k < _fftSize; k++)
        {
            _cosTable[k] = Math.Cos(2.0 * Math.PI * k / _fftSize);
            _sinTable[k] = Math.Sin(2.0 * Math.PI * k / _fftSize);
        }

        _referenceSymbols = referenceSymbols ?? Array.Empty<Complex[]>();
        _referenceBins = new int[_referenceSymbols.Length][];
        for (var g = 0; g < _referenceSymbols.Length; g++)
        {
            _referenceBins[g] = ResolveCarrierBins(_referenceSymbols[g]);
        }

        _window = _period * 4;
        _hop = _period;
        _lagTolerance = Math.Max(2, (int)Math.Ceiling(_period * LagToleranceRatio));
        _minRunSamples = Math.Max(_window, minRunSamples);
        _maxGapSamples = _period * 8;
        LastPeriod = _period;
    }

    /// <summary>
    /// 走査位置と区間追跡を初期化します。
    /// </summary>
    public void Reset()
    {
        _scanPos = 0;
        _runStart = -1;
        _lastGood = -1;
        _lastGoodLag = _period;
    }

    /// <summary>
    /// バッファ先頭が捨てられた分だけ内部位置をずらします。
    /// </summary>
    /// <param name="drop">先頭から捨てたサンプル数。</param>
    public void OnFrontDropped(int drop)
    {
        if (drop <= 0)
        {
            return;
        }

        _scanPos = Math.Max(0, _scanPos - drop);
        if (_runStart >= 0)
        {
            _runStart -= drop;
            _lastGood -= drop;
            if (_lastGood < 0)
            {
                _runStart = -1;
                _lastGood = -1;
            }
            else
            {
                _runStart = Math.Max(0, _runStart);
            }
        }
    }

    /// <summary>
    /// 未走査部分を調べ、十分長い無変調区間が終わっていれば FH 変調部の先頭位置を返します。
    /// </summary>
    /// <param name="left">L チャネル PCM（ヘッダーはモノラルで L に載る）。</param>
    /// <param name="anchor">FH 変調部の先頭サンプル位置（バッファ先頭基準）。</param>
    /// <returns>無変調区間の終端を確定できた場合 true。</returns>
    public bool TryAdvance(ReadOnlySpan<Complex> left, out int anchor)
    {
        anchor = -1;
        var maxLag = _period + _lagTolerance;
        while (_scanPos + _window + maxLag <= left.Length)
        {
            var (score, lag) = Score(left, _scanPos);
            if (score >= CorrelationThreshold)
            {
                if (_runStart < 0)
                {
                    _runStart = _scanPos;
                }

                _lastGood = _scanPos;
                _lastGoodLag = lag;
            }
            else if (_runStart >= 0 && _scanPos - _lastGood > _maxGapSamples)
            {
                var runLength = _lastGood + _window + _lastGoodLag - _runStart;
                var runStart = _runStart;
                var lastGood = _lastGood;
                var lastLag = _lastGoodLag;
                _runStart = -1;
                _lastGood = -1;
                if (runLength >= _minRunSamples)
                {
                    var end = RefineEnd(left, lastGood, lastLag);
                    var period = EstimatePeriod(left, runStart, end, lastLag);
                    if (TryMatchReference(left, end, period, out var symbolStart, out var inverted))
                    {
                        LastPeriod = period;
                        LastPolarityInverted = inverted;
                        anchor = SnapToSymbolPhase(end, symbolStart, period);
                        _scanPos += _hop;
                        return true;
                    }
                }
            }

            _scanPos += _hop;
        }

        return false;
    }

    /// <summary>
    /// 窓内の周期ラグ自己相関を、速度ずれ分のラグ幅で最大化して求めます。
    /// </summary>
    /// <param name="x">入力 PCM。</param>
    /// <param name="start">窓の先頭位置。</param>
    /// <returns>正規化自己相関の最大値と、そのときのラグ。</returns>
    private (double Score, int Lag) Score(ReadOnlySpan<Complex> x, int start)
    {
        var e0 = 0.0;
        for (var n = start; n < start + _window; n++)
        {
            var v = x[n].Real;
            e0 += v * v;
        }

        if (e0 / _window < MinMeanPower)
        {
            return (0.0, _period);
        }

        var best = double.NegativeInfinity;
        var bestLag = _period;
        for (var lag = _period - _lagTolerance; lag <= _period + _lagTolerance; lag++)
        {
            var num = 0.0;
            var e1 = 0.0;
            for (var n = start; n < start + _window; n++)
            {
                var b = x[n + lag].Real;
                num += x[n].Real * b;
                e1 += b * b;
            }

            var score = e1 <= 0.0 ? 0.0 : num / Math.Sqrt(e0 * e1);
            if (score > best)
            {
                best = score;
                bestLag = lag;
            }
        }

        return (best, bestLag);
    }

    /// <summary>
    /// 区間終端直前の 1 周期が無変調シンボルかを、キャリアへの電力集中とパイロット位相の整合で判定し、シンボル位相と極性を求めます。
    /// </summary>
    /// <param name="x">入力 PCM。</param>
    /// <param name="end">無変調区間の終端位置。</param>
    /// <param name="period">測定した繰り返し周期（速度ずれ分を公称長へ伸縮して照合する）。</param>
    /// <param name="symbolStart">シンボル先頭（CP 先頭）の入力位置（小数）。求まらない場合は NaN。</param>
    /// <param name="inverted">極性が反転して届いている場合 true。</param>
    /// <returns>無変調シンボルと判定した場合、または照合用波形が無い場合 true。</returns>
    /// <remarks>
    /// 時間波形の一致はアナログ経路の位相特性・極性反転で崩れるため判定に使わない。
    /// パイロットの位相は両隣のデータキャリアの位相（ビン位置で線形補間）と一致するはずで、遅延・極性・なだらかな位相特性はこの比較で打ち消される。
    /// ゼロ詰めの多い FH 変調部は全データキャリアが同じ QPSK 点になり、パイロットとの差が 45° 以上残るので除外できる。
    /// </remarks>
    private bool TryMatchReference(ReadOnlySpan<Complex> x, int end, double period, out double symbolStart, out bool inverted)
    {
        symbolStart = double.NaN;
        inverted = false;
        if (_referenceSymbols.Length == 0)
        {
            return true;
        }

        var span = (int)Math.Ceiling(period * 3) + (2 * Onta.Stream.StreamSpeedWarp.HalfTaps);
        var from = end - span;
        if (from < 0)
        {
            return false;
        }

        var local = x.Slice(from, span).ToArray();
        var segment = new Complex[_period];
        var step = period / _period;
        Onta.Stream.StreamSpeedWarp.Warp(local, local.Length, span - (2 * period), step, segment, _period);
        var es = 0.0;
        for (var n = 0; n < _period; n++)
        {
            es += segment[n].Real * segment[n].Real;
        }

        if (es <= 0.0)
        {
            return false;
        }

        for (var g = 0; g < _referenceSymbols.Length; g++)
        {
            var reference = _referenceSymbols[g];
            var bins = _referenceBins[g];
            if (reference.Length != _period || bins.Length < 3 || !IsUnmodulatedSpectrum(segment, bins))
            {
                continue;
            }

            var (score, shift) = BestCircularCorrelation(segment, reference, es);
            inverted = score < 0.0;
            if (Math.Abs(score) >= SymbolPhaseMinCorrelation)
            {
                // segment[n] ≒ ±reference[(n + shift) mod 周期] なので、reference の先頭は n = 周期 − shift
                var headIndex = (_period - shift) % _period;
                symbolStart = end - (2 * period) + (headIndex * step);
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 1 周期分の波形が、指定グリッドの無変調シンボル（全キャリア同相）のスペクトルかを判定します。
    /// </summary>
    /// <param name="segment">公称長へ伸縮した 1 周期分の波形。</param>
    /// <param name="bins">そのグリッドのキャリアビン（昇順）。</param>
    /// <returns>キャリアへ電力が集中し、パイロット位相が両隣と整合する場合 true。</returns>
    private bool IsUnmodulatedSpectrum(Complex[] segment, int[] bins)
    {
        // CP を含むシンボル内に収まる窓だけがビン中心のトーンになるため、キャリア集中度が最大の窓位置を探す
        var bestFraction = 0.0;
        var bestOffset = 0;
        var spectrum = new Complex[bins.Length];
        for (var offset = 0; offset < _period; offset += 2)
        {
            var fraction = CarrierFraction(segment, offset, bins, spectrum);
            if (fraction > bestFraction)
            {
                bestFraction = fraction;
                bestOffset = offset;
            }
        }

        if (bestFraction < MinCarrierFraction)
        {
            return false;
        }

        CarrierFraction(segment, bestOffset, bins, spectrum);
        var meanPower = 0.0;
        foreach (var value in spectrum)
        {
            meanPower += value.Magnitude * value.Magnitude;
        }

        meanPower /= spectrum.Length;
        var residualSum = 0.0;
        var pilots = 0;
        for (var i = 1; i < bins.Length - 1; i++)
        {
            var slot = i % 8;
            if (slot != 2 && slot != 6)
            {
                continue;
            }

            var below = spectrum[i - 1];
            var above = spectrum[i + 1];
            var minPower = MinNeighborPowerRatio * meanPower;
            if ((below.Magnitude * below.Magnitude) < minPower
                || (above.Magnitude * above.Magnitude) < minPower
                || (spectrum[i].Magnitude * spectrum[i].Magnitude) < minPower)
            {
                return false;
            }

            var t = (bins[i] - bins[i - 1]) / (double)(bins[i + 1] - bins[i - 1]);
            var predicted = below.Phase + (t * (above * Complex.Conjugate(below)).Phase);
            residualSum += Math.Abs(WrapPhase(spectrum[i].Phase - predicted));
            pilots++;
        }

        return pilots >= 2 && (residualSum / pilots) <= MaxPilotPhaseResidual;
    }

    /// <summary>
    /// 周期波形の指定位置から FFT 長の窓を取り、キャリアビンの複素振幅と全電力に占める割合を求めます。
    /// </summary>
    /// <param name="segment">1 周期分の波形（周期的に折り返して読む）。</param>
    /// <param name="offset">窓の開始位置。</param>
    /// <param name="bins">キャリアビン。</param>
    /// <param name="spectrum">各キャリアの複素振幅の書き込み先。</param>
    /// <returns>窓の電力に占めるキャリアの割合（0〜1）。</returns>
    private double CarrierFraction(Complex[] segment, int offset, int[] bins, Complex[] spectrum)
    {
        var energy = 0.0;
        for (var n = 0; n < _fftSize; n++)
        {
            var v = segment[(offset + n) % _period].Real;
            energy += v * v;
        }

        if (energy <= 0.0)
        {
            return 0.0;
        }

        var carrierEnergy = 0.0;
        for (var i = 0; i < bins.Length; i++)
        {
            var re = 0.0;
            var im = 0.0;
            var bin = bins[i];
            for (var n = 0; n < _fftSize; n++)
            {
                var v = segment[(offset + n) % _period].Real;
                var k = (bin * n) % _fftSize;
                re += v * _cosTable[k];
                im -= v * _sinTable[k];
            }

            spectrum[i] = new Complex(re, im);
            carrierEnergy += (re * re) + (im * im);
        }

        // 実信号の片側ビン電力は 2|X|²/N
        return 2.0 * carrierEnergy / (_fftSize * energy);
    }

    /// <summary>
    /// 1 周期分の波形と理想シンボルの巡回相関を全シフトで求め、絶対値最大のものを返します。
    /// </summary>
    /// <param name="segment">1 周期分の波形。</param>
    /// <param name="reference">理想無変調シンボル。</param>
    /// <param name="segmentEnergy">segment の電力。</param>
    /// <returns>正規化相関（負なら極性反転）とそのシフト量。</returns>
    private (double Score, int Shift) BestCircularCorrelation(Complex[] segment, Complex[] reference, double segmentEnergy)
    {
        var er = 0.0;
        for (var n = 0; n < _period; n++)
        {
            er += reference[n].Real * reference[n].Real;
        }

        if (er <= 0.0)
        {
            return (0.0, 0);
        }

        var norm = Math.Sqrt(segmentEnergy * er);
        var bestScore = 0.0;
        var bestShift = 0;
        for (var shift = 0; shift < _period; shift++)
        {
            var num = 0.0;
            for (var n = 0; n < _period; n++)
            {
                var k = n + shift;
                if (k >= _period)
                {
                    k -= _period;
                }

                num += segment[n].Real * reference[k].Real;
            }

            if (Math.Abs(num / norm) > Math.Abs(bestScore))
            {
                bestScore = num / norm;
                bestShift = shift;
            }
        }

        return (bestScore, bestShift);
    }

    /// <summary>
    /// 位相を −π〜π へ折り返します。
    /// </summary>
    /// <param name="phase">位相（ラジアン）。</param>
    /// <returns>折り返した位相。</returns>
    private static double WrapPhase(double phase)
    {
        phase %= 2.0 * Math.PI;
        if (phase > Math.PI)
        {
            phase -= 2.0 * Math.PI;
        }
        else if (phase < -Math.PI)
        {
            phase += 2.0 * Math.PI;
        }

        return phase;
    }

    /// <summary>
    /// 理想無変調シンボル（CP 込み）の本体からキャリアビンを取り出します。
    /// </summary>
    /// <param name="reference">理想無変調シンボル。</param>
    /// <returns>振幅のあるビン（昇順）。</returns>
    private int[] ResolveCarrierBins(Complex[] reference)
    {
        var cp = reference.Length - _fftSize;
        if (cp < 0)
        {
            return Array.Empty<int>();
        }

        var magnitudes = new double[(_fftSize / 2) + 1];
        var max = 0.0;
        for (var bin = 1; bin < magnitudes.Length; bin++)
        {
            var re = 0.0;
            var im = 0.0;
            for (var n = 0; n < _fftSize; n++)
            {
                var k = (bin * n) % _fftSize;
                re += reference[cp + n].Real * _cosTable[k];
                im -= reference[cp + n].Real * _sinTable[k];
            }

            magnitudes[bin] = Math.Sqrt((re * re) + (im * im));
            max = Math.Max(max, magnitudes[bin]);
        }

        var bins = new List<int>();
        for (var bin = 1; bin < magnitudes.Length; bin++)
        {
            if (magnitudes[bin] >= 0.5 * max)
            {
                bins.Add(bin);
            }
        }

        return bins.ToArray();
    }

    /// <summary>
    /// 無変調区間のシンボル位相から、終端（FH 変調部の先頭）を正確に決めます。
    /// </summary>
    /// <param name="end">周期差分で求めた終端（帯域制限で数サンプルにじむ）。</param>
    /// <param name="symbolStart">無変調シンボル先頭の入力位置。NaN なら補正しない。</param>
    /// <param name="period">測定した繰り返し周期。</param>
    /// <returns>シンボル位相に合わせた終端位置。</returns>
    /// <remarks>FH 無変調区間はシンボル先頭から始まり長さが決まっているため、終端の位相は一意に決まる。</remarks>
    private int SnapToSymbolPhase(int end, double symbolStart, double period)
    {
        if (double.IsNaN(symbolStart) || _tailUnmodulatedSamples <= 0)
        {
            return end;
        }

        var phaseEnd = symbolStart + (_tailUnmodulatedSamples * (period / _period));
        var cycles = Math.Round((end - phaseEnd) / period);
        return (int)Math.Round(phaseEnd + (cycles * period));
    }

    /// <summary>
    /// 無変調区間の終端側（FH 無変調 1 秒の内側）で、先頭の 1 周期が何周期先でどこに再び現れるかを小数精度で求め、繰り返し周期を測ります。
    /// </summary>
    /// <param name="x">入力 PCM。</param>
    /// <param name="runStart">無変調区間の先頭（窓単位の目安）。</param>
    /// <param name="end">無変調区間の終端。</param>
    /// <param name="coarseLag">自己相関で得た整数ラグ。</param>
    /// <returns>繰り返し周期（サンプル）。測れない場合は coarseLag。</returns>
    /// <remarks>プリアンブルと FH 無変調の境目では位相が飛ぶため、終端から最短区間長ぶんだけを使う。</remarks>
    private double EstimatePeriod(ReadOnlySpan<Complex> x, int runStart, int end, int coarseLag)
    {
        var a = Math.Max(runStart, end - _minRunSamples + _period);
        var last = end - _period - 8;
        double period = coarseLag;
        if (last - a < _period * 4)
        {
            return period;
        }

        // 整数ラグの誤差（最大 0.5）を短い距離で詰めてから、長い距離で精度を上げる
        foreach (var cycles in new[] { 8, 32, int.MaxValue })
        {
            var range = cycles == 8 ? (int)Math.Ceiling(8 * 0.6) + 2 : 4;
            var k = Math.Min(cycles, (int)Math.Floor((last - range - 1 - a) / period));
            if (k < 1)
            {
                break;
            }

            var predicted = (int)Math.Round(a + (k * period));
            var bestShift = 0;
            var scores = new double[(2 * range) + 1];
            for (var s = -range; s <= range; s++)
            {
                scores[s + range] = NormalizedCorrelation(x, a, predicted + s, _period);
                if (scores[s + range] > scores[bestShift + range])
                {
                    bestShift = s;
                }
            }

            var offset = 0.0;
            if (bestShift > -range && bestShift < range)
            {
                var ym = scores[bestShift + range - 1];
                var y0 = scores[bestShift + range];
                var yp = scores[bestShift + range + 1];
                var denom = ym - (2 * y0) + yp;
                if (Math.Abs(denom) > 1e-12)
                {
                    offset = Math.Clamp(0.5 * (ym - yp) / denom, -0.5, 0.5);
                }
            }

            period = (predicted + bestShift + offset - a) / k;
        }

        return period;
    }

    /// <summary>
    /// 2 区間の正規化相関を求めます。
    /// </summary>
    /// <param name="x">入力 PCM。</param>
    /// <param name="a">区間 1 の先頭。</param>
    /// <param name="b">区間 2 の先頭。</param>
    /// <param name="length">区間長。</param>
    /// <returns>−1〜1 の相関値（範囲外や無音は 0）。</returns>
    private static double NormalizedCorrelation(ReadOnlySpan<Complex> x, int a, int b, int length)
    {
        if (a < 0 || b < 0 || a + length > x.Length || b + length > x.Length)
        {
            return 0.0;
        }

        var num = 0.0;
        var ea = 0.0;
        var eb = 0.0;
        for (var n = 0; n < length; n++)
        {
            var va = x[a + n].Real;
            var vb = x[b + n].Real;
            num += va * vb;
            ea += va * va;
            eb += vb * vb;
        }

        return ea <= 0.0 || eb <= 0.0 ? 0.0 : num / Math.Sqrt(ea * eb);
    }

    /// <summary>
    /// 周期差分（x[n]−x[n−lag]）の電力が急に増える点をサンプル単位で探し、無変調区間の終端とします。
    /// </summary>
    /// <param name="x">入力 PCM。</param>
    /// <param name="lastGood">最後に無変調と判定した窓の先頭位置。</param>
    /// <param name="lag">その窓で相関が最大だったラグ。</param>
    /// <returns>無変調区間の終端（FH 変調部の先頭）位置。</returns>
    private int RefineEnd(ReadOnlySpan<Complex> x, int lastGood, int lag)
    {
        var coarse = lastGood + _window + lag;
        var segment = _period;
        var from = Math.Max(lag + segment, coarse - _window);
        var to = Math.Min(x.Length - segment, coarse + (2 * _window));
        if (to <= from)
        {
            return Math.Clamp(coarse, 0, x.Length);
        }

        var baseIndex = from - segment;
        var count = (to + segment) - baseIndex;
        var prefix = new double[count + 1];
        for (var i = 0; i < count; i++)
        {
            var n = baseIndex + i;
            var d = x[n].Real - x[n - lag].Real;
            prefix[i + 1] = prefix[i] + (d * d);
        }

        // 前後で分散が切り替わる 1 点を、2 区間の対数尤度が最大になる位置として求める
        // （変調部の先頭は電力が小さいことがあるため、固定幅の差ではなく分散の切り替わりで判定する）
        var total = prefix[count];
        var eps = (1e-6 * total / count) + 1e-18;
        var bestB = coarse;
        var bestCost = double.PositiveInfinity;
        for (var b = from; b <= to; b++)
        {
            var i = b - baseIndex;
            var before = prefix[i];
            var after = total - before;
            var n1 = (double)i;
            var n2 = (double)(count - i);
            var cost = (n1 * Math.Log((before / n1) + eps)) + (n2 * Math.Log((after / n2) + eps));
            if (cost < bestCost)
            {
                bestCost = cost;
                bestB = b;
            }
        }

        return bestB;
    }
}
