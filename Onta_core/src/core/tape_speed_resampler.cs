using System.Numerics;
using Onta.Stream;

namespace Onta.Core;

/// <summary>
/// テープ速度のずれを一定比で戻す逐次リサンプラーです（窓付き sinc 補間）。
/// </summary>
internal sealed class TapeSpeedResampler
{
    /// <summary>補正不要とみなす速度比のずれ（これ以下なら素通しと同等）。</summary>
    public const double NegligibleDeviation = 2e-5;

    private readonly bool _stereo;
    private Complex[] _rawLeft = new Complex[8192];
    private Complex[] _rawRight;
    private int _rawCount;
    private double _pos;

    /// <summary>
    /// リサンプラーを初期化します。
    /// </summary>
    /// <param name="step">出力 1 点あたりに進む入力サンプル数（テープが速いと 1 より大きい）。</param>
    /// <param name="stereo">R チャネルも変換する場合 true。</param>
    public TapeSpeedResampler(double step, bool stereo)
    {
        Step = step;
        _stereo = stereo;
        _rawRight = stereo ? new Complex[8192] : Array.Empty<Complex>();
    }

    /// <summary>
    /// 出力 1 点あたりに進む入力サンプル数です。途中で変えると以降の出力から反映されます。
    /// </summary>
    public double Step { get; set; }

    /// <summary>
    /// 入力を追加し、補間できるところまでを出力します（出力は入力より約 16 サンプル遅れる）。
    /// </summary>
    /// <param name="left">L 入力。</param>
    /// <param name="right">R 入力（モノラル時は無視）。</param>
    /// <param name="outLeft">L 出力。</param>
    /// <param name="outRight">R 出力（モノラル時は空）。</param>
    public void Process(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right, out Complex[] outLeft, out Complex[] outRight)
    {
        Append(left, right);
        var lastPos = _rawCount - StreamSpeedWarp.HalfTaps - 1;
        if (_pos > lastPos)
        {
            outLeft = Array.Empty<Complex>();
            outRight = Array.Empty<Complex>();
            return;
        }

        var count = (int)Math.Floor((lastPos - _pos) / Step) + 1;
        outLeft = new Complex[count];
        StreamSpeedWarp.Warp(_rawLeft, _rawCount, _pos, Step, outLeft, count);
        if (_stereo)
        {
            outRight = new Complex[count];
            StreamSpeedWarp.Warp(_rawRight, _rawCount, _pos, Step, outRight, count);
        }
        else
        {
            outRight = Array.Empty<Complex>();
        }

        _pos += count * Step;
        var keepFrom = Math.Max(0, (int)Math.Floor(_pos) - StreamSpeedWarp.HalfTaps);
        if (keepFrom > 0)
        {
            var remain = _rawCount - keepFrom;
            Array.Copy(_rawLeft, keepFrom, _rawLeft, 0, remain);
            if (_stereo)
            {
                Array.Copy(_rawRight, keepFrom, _rawRight, 0, remain);
            }

            _rawCount = remain;
            _pos -= keepFrom;
        }
    }

    /// <summary>
    /// 配列全体を一定比でリサンプルします。
    /// </summary>
    /// <param name="source">入力 PCM。</param>
    /// <param name="count">入力の有効サンプル数。</param>
    /// <param name="step">出力 1 点あたりに進む入力サンプル数。</param>
    /// <returns>リサンプル後の PCM。</returns>
    public static Complex[] Resample(Complex[] source, int count, double step)
    {
        if (count <= 0 || step <= 0.0)
        {
            return Array.Empty<Complex>();
        }

        var n = (int)Math.Floor((count - 1) / step) + 1;
        var dest = new Complex[n];
        StreamSpeedWarp.Warp(source, count, 0.0, step, dest, n);
        return dest;
    }

    /// <summary>
    /// 入力を内部の生サンプル列へ追記します。
    /// </summary>
    /// <param name="left">L 入力。</param>
    /// <param name="right">R 入力。</param>
    private void Append(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right)
    {
        var needed = _rawCount + left.Length;
        if (_rawLeft.Length < needed)
        {
            var size = Math.Max(needed, _rawLeft.Length * 2);
            Array.Resize(ref _rawLeft, size);
            if (_stereo)
            {
                Array.Resize(ref _rawRight, size);
            }
        }

        left.CopyTo(_rawLeft.AsSpan(_rawCount));
        if (_stereo)
        {
            var n = Math.Min(left.Length, right.Length);
            right[..n].CopyTo(_rawRight.AsSpan(_rawCount));
        }

        _rawCount = needed;
    }
}
