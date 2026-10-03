using Onta.Stream.Opus;

namespace Onta.Stream;

/// <summary>
/// ストリーム再生の Opus 復号と、再生バッファの充填量に合わせた 20 ms 単位の長さ調整を行います。
/// バッファが目標より少なければ PLC（パケット損失補償）フレームを差し込み、多ければ連続 2 フレームをクロスフェードで 1 フレームに詰めます。
/// 失われたパケットの区間も PLC で埋めます。
/// </summary>
public sealed class StreamPlayoutRegulator : IDisposable
{
    /// <summary>既定の目標充填量（秒）。</summary>
    public const double DefaultTargetSeconds = 1.6;

    /// <summary>既定の不感帯（目標 ± 秒）。</summary>
    public const double DefaultToleranceSeconds = 0.2;

    /// <summary>調整のあと次の調整までに挟む復号フレーム数。1 回の調整は 20 ms なので、速度の変化は最大 20 ms / 100 ms に収まる。</summary>
    public const int DefaultAdjustIntervalFrames = 5;

    /// <summary>復調が遅れてフレームが届かない間に PLC で埋める上限（20 フレーム = 0.4 秒）。長く続けると不自然な音が続くため、それ以降は無音にする。</summary>
    public const int DefaultMaxStallConcealFrames = 20;

    private readonly OpusDecoder _decoder;
    private readonly int _frameSamples;
    private bool _primed;
    private int _sinceAdjust;
    private int _stallFrames;
    private int _stallCredit;
    private bool _disposed;

    /// <summary>
    /// 出力サンプリング周波数を指定して構築します。
    /// </summary>
    /// <param name="outputSampleRate">復号音声のサンプリング周波数。</param>
    public StreamPlayoutRegulator(int outputSampleRate)
    {
        var rate = Math.Max(1, outputSampleRate);
        _decoder = new OpusDecoder(rate);
        _frameSamples = (int)Math.Round(rate * (OpusEncoder.FrameSamplesPerChannel / (double)OpusEncoder.OpusSampleRate));
        TargetFrames = (int)Math.Round(rate * DefaultTargetSeconds);
        ToleranceFrames = (int)Math.Round(rate * DefaultToleranceSeconds);
    }

    /// <summary>
    /// 再生側に溜まっている未再生サンプル数（片チャネル・出力サンプリング周波数）を返す関数。
    /// null なら長さ調整・無音の先詰めは行わず、失われたパケットの PLC だけ行う。
    /// </summary>
    public Func<int>? BufferedFrames { get; set; }

    /// <summary>目標充填量（片チャネルのサンプル数）。</summary>
    public int TargetFrames { get; set; }

    /// <summary>目標からの不感帯（片チャネルのサンプル数）。この範囲内では調整しない。</summary>
    public int ToleranceFrames { get; set; }

    /// <summary>調整のあと次の調整までに挟む復号フレーム数。</summary>
    public int AdjustIntervalFrames { get; set; } = DefaultAdjustIntervalFrames;

    /// <summary>1 フレーム（20 ms）の片チャネルサンプル数。</summary>
    public int FrameSamples => _frameSamples;

    /// <summary>充填不足で差し込んだ PLC フレーム数。</summary>
    public int InsertedFrames { get; private set; }

    /// <summary>充填過多で詰めた（2 → 1 にした）フレーム数。</summary>
    public int MergedFrames { get; private set; }

    /// <summary>失われたパケットの区間を埋めた PLC フレーム数。</summary>
    public int ConcealedFrames { get; private set; }

    /// <summary>復調が遅れてフレームが届かない間に PLC で埋められる上限フレーム数。</summary>
    public int MaxStallConcealFrames { get; set; } = DefaultMaxStallConcealFrames;

    /// <summary>復調の遅れ（フレームが届かない間）を埋めた PLC フレーム数。</summary>
    public int StallConcealedFrames { get; private set; }

    /// <summary>
    /// 1 パケット分の Opus フレームを復号し、充填量に応じて PLC の差し込み・2 フレームの結合を行って output へ追加します。
    /// </summary>
    /// <param name="frames">Opus フレーム列。</param>
    /// <param name="output">未再生の出力（今回の Pump で溜めたもの。充填量の計算に含める）。</param>
    public void Decode(IReadOnlyList<byte[]> frames, List<(double[] Left, double[] Right)> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frames.Count == 0)
        {
            return;
        }

        _stallFrames = 0;
        _stallCredit = 0;
        PrefillOnUnderrun(output);
        (double[] Left, double[] Right)? held = null;
        foreach (var frame in frames)
        {
            if (_decoder.DecodeToPcm(frame, out var l, out var r) <= 0)
            {
                continue;
            }

            _primed = true;
            _sinceAdjust++;
            if (held is { } a)
            {
                output.Add(CrossFade(a, (l, r)));
                held = null;
                MergedFrames++;
                _sinceAdjust = 0;
                continue;
            }

            var level = Level(output);
            var canAdjust = BufferedFrames != null && _sinceAdjust >= AdjustIntervalFrames;
            if (canAdjust && level > TargetFrames + ToleranceFrames)
            {
                held = (l, r);
                continue;
            }

            output.Add((l, r));
            if (canAdjust && level + l.Length < TargetFrames - ToleranceFrames && Conceal(output))
            {
                InsertedFrames++;
                _sinceAdjust = 0;
            }
        }

        if (held is { } rest)
        {
            output.Add(rest);
        }
    }

    /// <summary>
    /// 失われたパケットの区間を PLC フレームで埋めます。まだ 1 フレームも復号していなければ何もしません。
    /// 直前の復調の遅れで <see cref="ConcealStall"/> が埋めた分は、同じ区間を二重に埋めないよう差し引きます。
    /// </summary>
    /// <param name="frameCount">埋めるフレーム数。</param>
    /// <param name="output">未再生の出力。</param>
    public void ConcealLost(int frameCount, List<(double[] Left, double[] Right)> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var credit = Math.Min(Math.Max(0, frameCount), _stallCredit);
        _stallCredit -= credit;
        frameCount -= credit;
        if (!_primed || frameCount <= 0)
        {
            return;
        }

        PrefillOnUnderrun(output);
        for (var i = 0; i < frameCount; i++)
        {
            if (Conceal(output))
            {
                ConcealedFrames++;
            }
        }
    }

    /// <summary>
    /// 復調が遅れてフレームが届かない間、PLC で 1 フレーム埋めます。
    /// 次にフレームが届くまでに <see cref="MaxStallConcealFrames"/> を超えては埋めず、まだ 1 フレームも復号していなければ何もしません。
    /// </summary>
    /// <param name="output">追加先。</param>
    /// <returns>埋めたら true。</returns>
    public bool ConcealStall(List<(double[] Left, double[] Right)> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_primed || _stallFrames >= MaxStallConcealFrames || !Conceal(output))
        {
            return false;
        }

        _stallFrames++;
        _stallCredit++;
        StallConcealedFrames++;
        return true;
    }

    /// <summary>
    /// PLC で 1 フレーム作って output へ追加します。
    /// </summary>
    /// <param name="output">追加先。</param>
    /// <returns>追加できたら true。</returns>
    private bool Conceal(List<(double[] Left, double[] Right)> output)
    {
        if (_decoder.ConcealToPcm(out var l, out var r) <= 0)
        {
            return false;
        }

        output.Add((l, r));
        return true;
    }

    /// <summary>
    /// 再生バッファがほぼ空（開始直後・長い途切れの後）なら、目標充填量まで無音を先に詰めます。
    /// </summary>
    /// <param name="output">未再生の出力。</param>
    private void PrefillOnUnderrun(List<(double[] Left, double[] Right)> output)
    {
        if (BufferedFrames == null)
        {
            return;
        }

        var level = Level(output);
        if (level >= TargetFrames / 4)
        {
            return;
        }

        var silence = TargetFrames - level;
        if (silence > 0)
        {
            output.Insert(0, (new double[silence], new double[silence]));
        }
    }

    /// <summary>
    /// 再生側の未再生サンプル数と、まだ渡していない output の合計を返します。
    /// </summary>
    /// <param name="output">未再生の出力。</param>
    /// <returns>片チャネルのサンプル数。</returns>
    private int Level(List<(double[] Left, double[] Right)> output)
    {
        var level = BufferedFrames?.Invoke() ?? 0;
        foreach (var (l, _) in output)
        {
            level += l.Length;
        }

        return level;
    }

    /// <summary>
    /// 2 フレームを、前者から後者へレイズドコサインでつないだ 1 フレームにします。
    /// </summary>
    /// <param name="a">先のフレーム。</param>
    /// <param name="b">後のフレーム。</param>
    /// <returns>結合したフレーム（長さは短い方）。</returns>
    private static (double[] Left, double[] Right) CrossFade((double[] Left, double[] Right) a, (double[] Left, double[] Right) b)
    {
        var n = Math.Min(a.Left.Length, b.Left.Length);
        var left = new double[n];
        var right = new double[n];
        for (var i = 0; i < n; i++)
        {
            var w = 0.5 - (0.5 * Math.Cos(Math.PI * (i + 0.5) / n));
            left[i] = (a.Left[i] * (1.0 - w)) + (b.Left[i] * w);
            right[i] = (a.Right[i] * (1.0 - w)) + (b.Right[i] * w);
        }

        return (left, right);
    }

    /// <summary>
    /// Opus デコーダーを解放します。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _decoder.Dispose();
    }
}
