using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 受信入力の信号の途中に挟まった短い無音（送信側の再生バッファ切れによる音切れ）を検出して取り除きます。
/// </summary>
/// <remarks>
/// 送信側の音切れは無音が「挿入」されるだけでデータは欠けないため、無音を詰めれば以降のシンボル位置が元に戻る。
/// 詰めずにおくと 1 シンボルより長いずれになり、長いブロックの残り全体が復号できなくなる。
/// テープのドロップアウト（時間は進み信号が弱まるだけ）を誤って詰めないよう、雑音の床まで落ちた区間だけを対象にする。
/// </remarks>
public sealed class InputSilenceGapRemover
{
    /// <summary>無音判定に使う短時間 RMS の窓長（サンプル）です。</summary>
    public const int EnvelopeWindow = 32;

    /// <summary>窓全体が無音になってから、無音とみなすまでの最短連続長（サンプル）です。</summary>
    public const int MinSilentRun = 64;

    /// <summary>詰める無音の最大長（秒）です。これより長い無音は送信の切れ目とみなして残します。</summary>
    public const double MaxGapSeconds = 0.2;

    /// <summary>無音とみなす短時間 RMS の、直前の信号レベルに対する比（-30 dB）です。</summary>
    public const double SilenceRatio = 0.03;

    /// <summary>信号ありとみなす最低レベル（短時間 RMS）です。これ未満では無音検出をしません。</summary>
    public const double MinSignalLevel = 0.002;

    /// <summary>信号レベル追従の平滑化係数（約 46 ms）です。</summary>
    private const double LevelAlpha = 1.0 / 2048.0;

    private readonly bool _stereo;
    private readonly int _maxSilentRun;
    private readonly double[] _power = new double[EnvelopeWindow];
    private int _powerIndex;
    private double _powerSum;
    private readonly List<Complex> _heldLeft = [];
    private readonly List<Complex> _heldRight = [];
    private int _gapStart = -1;
    private int _silentRun;
    private bool _longSilence;
    private double _signalLevel;

    /// <summary>
    /// 指定サンプルレート・チャネル構成用に初期化します。
    /// </summary>
    /// <param name="sampleRate">入力のサンプリング周波数（Hz）。</param>
    /// <param name="stereo">R チャネルも入力するとき true。</param>
    public InputSilenceGapRemover(int sampleRate, bool stereo)
    {
        _stereo = stereo;
        _maxSilentRun = (int)Math.Round(Math.Max(1, sampleRate) * MaxGapSeconds);
    }

    /// <summary>これまでに取り除いた無音の合計サンプル数です。</summary>
    public long RemovedSamples { get; private set; }

    /// <summary>これまでに取り除いた無音の箇所数です。</summary>
    public int RemovedGapCount { get; private set; }

    /// <summary>
    /// サンプル列を処理し、無音を取り除いて確定した分を返します（無音判定待ちの末尾は次回へ持ち越します）。
    /// </summary>
    /// <param name="left">L チャネルのサンプル列。</param>
    /// <param name="right">R チャネルのサンプル列（モノラル時は参照しません）。</param>
    /// <param name="outLeft">確定した L チャネルのサンプル列。</param>
    /// <param name="outRight">確定した R チャネルのサンプル列（モノラル時は空）。</param>
    public void Process(ReadOnlySpan<Complex> left, ReadOnlySpan<Complex> right, out Complex[] outLeft, out Complex[] outRight)
    {
        for (var i = 0; i < left.Length; i++)
        {
            var l = left[i];
            var r = _stereo ? right[i] : Complex.Zero;
            AddSample(l, r);
        }

        var keep = _gapStart >= 0 ? _heldLeft.Count - _gapStart : EnvelopeWindow - 1;
        var emit = Math.Max(0, _heldLeft.Count - keep);
        outLeft = TakeFront(_heldLeft, emit);
        outRight = _stereo ? TakeFront(_heldRight, emit) : [];
        if (_gapStart >= 0)
        {
            _gapStart -= emit;
        }
    }

    /// <summary>
    /// 持ち越し中のサンプルをすべて返します（入力終端で呼びます）。
    /// </summary>
    /// <param name="outLeft">残りの L チャネルのサンプル列。</param>
    /// <param name="outRight">残りの R チャネルのサンプル列（モノラル時は空）。</param>
    public void Flush(out Complex[] outLeft, out Complex[] outRight)
    {
        var count = _heldLeft.Count;
        outLeft = TakeFront(_heldLeft, count);
        outRight = _stereo ? TakeFront(_heldRight, count) : [];
        _gapStart = -1;
        _silentRun = 0;
    }

    /// <summary>
    /// 一括入力（WAV 全体など）から無音を取り除いた新しい配列を返します。
    /// </summary>
    /// <param name="left">L チャネルのサンプル列。</param>
    /// <param name="right">R チャネルのサンプル列。空ならモノラルとして扱います。</param>
    /// <param name="sampleRate">入力のサンプリング周波数（Hz）。</param>
    /// <returns>無音を詰めた L/R サンプル列と、取り除いた箇所数。</returns>
    public static (Complex[] Left, Complex[] Right, int RemovedGapCount) RemoveGaps(Complex[] left, Complex[] right, int sampleRate)
    {
        var stereo = right.Length == left.Length && right.Length > 0;
        var remover = new InputSilenceGapRemover(sampleRate, stereo);
        remover.Process(left, right, out var bodyLeft, out var bodyRight);
        remover.Flush(out var tailLeft, out var tailRight);
        if (remover.RemovedGapCount == 0)
        {
            return (left, right, 0);
        }

        return ([.. bodyLeft, .. tailLeft], stereo ? [.. bodyRight, .. tailRight] : right, remover.RemovedGapCount);
    }

    /// <summary>
    /// 1 サンプルを取り込み、無音区間の開始・終了を判定します。
    /// </summary>
    /// <param name="l">L チャネルのサンプル。</param>
    /// <param name="r">R チャネルのサンプル（モノラル時は 0）。</param>
    private void AddSample(Complex l, Complex r)
    {
        var p = _stereo ? 0.5 * ((l.Real * l.Real) + (r.Real * r.Real)) : l.Real * l.Real;
        _powerSum += p - _power[_powerIndex];
        _power[_powerIndex] = p;
        _powerIndex = (_powerIndex + 1) % EnvelopeWindow;
        var envelope = Math.Sqrt(Math.Max(0.0, _powerSum) / EnvelopeWindow);

        _heldLeft.Add(l);
        if (_stereo)
        {
            _heldRight.Add(r);
        }

        var silent = _signalLevel >= MinSignalLevel && envelope < _signalLevel * SilenceRatio;
        if (silent)
        {
            if (_gapStart < 0 && !_longSilence)
            {
                // 窓全体が無音になった時点なので、無音は窓長ぶん前から始まっている
                _gapStart = Math.Max(0, _heldLeft.Count - EnvelopeWindow);
                _silentRun = 0;
            }

            if (_gapStart >= 0 && ++_silentRun > _maxSilentRun)
            {
                _gapStart = -1;
                _longSilence = true;
            }

            return;
        }

        if (_gapStart >= 0 && _silentRun >= MinSilentRun)
        {
            // 現在のサンプルで信号が戻ったので、その直前までを詰める
            var removeCount = _heldLeft.Count - 1 - _gapStart;
            _heldLeft.RemoveRange(_gapStart, removeCount);
            if (_stereo)
            {
                _heldRight.RemoveRange(_gapStart, removeCount);
            }

            RemovedSamples += removeCount;
            RemovedGapCount++;
        }

        _gapStart = -1;
        _silentRun = 0;
        _longSilence = false;
        _signalLevel += (envelope - _signalLevel) * LevelAlpha;
    }

    /// <summary>
    /// リスト先頭から指定数を取り出して配列で返します。
    /// </summary>
    /// <param name="list">取り出し元。</param>
    /// <param name="count">取り出す数。</param>
    /// <returns>取り出したサンプル列。</returns>
    private static Complex[] TakeFront(List<Complex> list, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var result = list.GetRange(0, count).ToArray();
        list.RemoveRange(0, count);
        return result;
    }
}
