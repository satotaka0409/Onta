using System.Numerics;

namespace Onta.Core;

/// <summary>
/// 一括復号（WAV 入力）の前に、FH 手前の無変調区間からテープ速度と FH 位置を求め、PCM を送信時の時間軸・先頭位置へ揃えます。
/// </summary>
internal static class ReceiveAlignment
{
    /// <summary>位置合わせでずらさずに済ませる誤差（サンプル）。</summary>
    private const int ToleranceSamples = 16;

    /// <summary>
    /// 最初に見つかった FH アンカーを基準に、速度補正と先頭位置合わせを行います。
    /// </summary>
    /// <param name="codec">復号に使うコーデック（周期・オフセットの取得用）。</param>
    /// <param name="left">L PCM（補正後に置き換え）。</param>
    /// <param name="right">R PCM（補正後に置き換え。モノラル時は空のまま）。</param>
    /// <param name="speedStep">適用した速度比（公称 1 サンプルあたりの入力サンプル数）。未検出時は 1。</param>
    /// <returns>アンカーを見つけて揃えた場合 true。見つからなければ入力はそのまま。</returns>
    public static bool TryAlign(
        FileWavCodec codec,
        ref Complex[] left,
        ref Complex[] right,
        out double speedStep)
    {
        speedStep = 1.0;
        var detector = codec.CreateAnchorDetector();
        if (!detector.TryAdvance(left, out var anchor))
        {
            return false;
        }

        var step = detector.LastPeriod / detector.NominalPeriod;
        if (Math.Abs(step - 1.0) > TapeSpeedResampler.NegligibleDeviation)
        {
            left = TapeSpeedResampler.Resample(left, left.Length, step);
            if (right.Length > 0)
            {
                right = TapeSpeedResampler.Resample(right, right.Length, step);
            }

            anchor = (int)Math.Round(anchor / step);
            speedStep = step;
        }

        if (detector.LastPolarityInverted)
        {
            left = Negate(left);
            right = right.Length > 0 ? Negate(right) : right;
        }

        var shift = anchor - codec.FileHeaderDataOffsetSamples;
        if (shift > ToleranceSamples)
        {
            left = left[shift..];
            right = right.Length > 0 ? right[Math.Min(shift, right.Length)..] : right;
        }
        else if (shift < -ToleranceSamples)
        {
            left = PrependSilence(left, -shift);
            right = right.Length > 0 ? PrependSilence(right, -shift) : right;
        }

        return true;
    }

    /// <summary>
    /// 符号を反転した配列を返します（極性反転の補正用）。
    /// </summary>
    /// <param name="samples">元の PCM。</param>
    /// <returns>反転した PCM。</returns>
    private static Complex[] Negate(Complex[] samples)
    {
        var result = new Complex[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            result[i] = -samples[i];
        }

        return result;
    }

    /// <summary>
    /// 先頭へ無音を足した配列を返します。
    /// </summary>
    /// <param name="samples">元の PCM。</param>
    /// <param name="count">足す無音サンプル数。</param>
    /// <returns>無音付きの PCM。</returns>
    private static Complex[] PrependSilence(Complex[] samples, int count)
    {
        var result = new Complex[samples.Length + count];
        Array.Copy(samples, 0, result, count, samples.Length);
        return result;
    }
}
