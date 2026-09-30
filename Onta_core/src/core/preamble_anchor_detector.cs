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

    /// <summary>テープ速度ずれを吸収するラグの探索幅（周期比）。</summary>
    private const double LagToleranceRatio = 0.02;

    /// <summary>区間終端の 1 周期を理想無変調シンボルと照合するときの正規化相関の下限。</summary>
    private const double ReferenceMatchThreshold = 0.9;

    private readonly Complex[][] _referenceSymbols;
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
    /// 検出器を初期化します。
    /// </summary>
    /// <param name="symbolSamples">無変調区間の繰り返し周期（ヘッダー OFDM 1 シンボル、CP 込み）。</param>
    /// <param name="minRunSamples">アンカーとみなす無変調区間の最短長。BH（0.3 秒）を除外する長さを指定します。</param>
    /// <param name="referenceSymbols">無変調 1 シンボルの理想波形（グリッド候補ごと）。空なら照合しません。</param>
    /// <remarks>ゼロ詰めの多いヘッダー変調部も同一シンボルが続いて周期的に見えるため、理想波形との照合で除外する。</remarks>
    public PreambleAnchorDetector(int symbolSamples, int minRunSamples, Complex[][]? referenceSymbols = null)
    {
        _referenceSymbols = referenceSymbols ?? Array.Empty<Complex[]>();
        _period = Math.Max(16, symbolSamples);
        _window = _period * 4;
        _hop = _period;
        _lagTolerance = Math.Max(2, (int)Math.Ceiling(_period * LagToleranceRatio));
        _minRunSamples = Math.Max(_window, minRunSamples);
        _maxGapSamples = _period * 8;
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
                var lastGood = _lastGood;
                var lastLag = _lastGoodLag;
                _runStart = -1;
                _lastGood = -1;
                if (runLength >= _minRunSamples)
                {
                    var end = RefineEnd(left, lastGood, lastLag);
                    if (MatchesReference(left, end))
                    {
                        anchor = end;
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
    /// 区間終端直前の 1 周期が、いずれかの理想無変調シンボルの巡回シフトと一致するかを調べます。
    /// </summary>
    /// <param name="x">入力 PCM。</param>
    /// <param name="end">無変調区間の終端位置。</param>
    /// <returns>一致した場合、または照合用波形が無い場合 true。</returns>
    private bool MatchesReference(ReadOnlySpan<Complex> x, int end)
    {
        if (_referenceSymbols.Length == 0)
        {
            return true;
        }

        var start = end - (2 * _period);
        if (start < 0)
        {
            return false;
        }

        var segment = x.Slice(start, _period);
        var es = 0.0;
        for (var n = 0; n < _period; n++)
        {
            es += segment[n].Real * segment[n].Real;
        }

        if (es <= 0.0)
        {
            return false;
        }

        foreach (var reference in _referenceSymbols)
        {
            if (reference.Length != _period)
            {
                continue;
            }

            var er = 0.0;
            for (var n = 0; n < _period; n++)
            {
                er += reference[n].Real * reference[n].Real;
            }

            if (er <= 0.0)
            {
                continue;
            }

            var norm = Math.Sqrt(es * er);
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

                if (num / norm >= ReferenceMatchThreshold)
                {
                    return true;
                }
            }
        }

        return false;
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

        var bestB = coarse;
        var bestContrast = double.NegativeInfinity;
        for (var b = from; b <= to; b++)
        {
            var i = b - baseIndex;
            var before = prefix[i] - prefix[i - segment];
            var after = prefix[i + segment] - prefix[i];
            var contrast = after - before;
            if (contrast > bestContrast)
            {
                bestContrast = contrast;
                bestB = b;
            }
        }

        return bestB;
    }
}
