namespace Onta.Performance;

/// <summary>
/// 性能測定の受信レベル判定です。
/// </summary>
internal static class PerformanceSignalLevel
{
    /// <summary>無音とみなす RMS（−60 dBFS。ストリーム受信の無信号判定と同じ）。</summary>
    public const double SilenceRms = 1e-3;

    /// <summary><see cref="IsSteady"/> で区間を分ける数です。</summary>
    public const int SteadySegments = 8;

    /// <summary><see cref="IsSteady"/> で各小区間に求める RMS の、区間全体の RMS に対する比（−12 dB）です。</summary>
    public const double SteadyMinRatio = 0.25;

    /// <summary>
    /// 区間の RMS（平均を除く）が <see cref="SilenceRms"/> 未満なら無音と判定します。
    /// </summary>
    /// <param name="pcm">判定する PCM。</param>
    /// <returns>無音（または空）のとき true。</returns>
    public static bool IsSilent(ReadOnlySpan<double> pcm)
    {
        if (pcm.IsEmpty)
        {
            return true;
        }

        var mean = Mean(pcm);
        return MeanSquare(pcm, mean) < SilenceRms * SilenceRms;
    }

    /// <summary>
    /// 区間全体に信号が続いているかを判定します。区間を <see cref="SteadySegments"/> 個に分け、
    /// どの小区間も無音でなく、RMS が区間全体の <see cref="SteadyMinRatio"/> 倍以上なら true です。
    /// 信号の始まり・終わりを含む区間では、端に残った短い信号の代わりに雑音のピークを周波数として拾うため除きます。
    /// </summary>
    /// <param name="pcm">判定する PCM。</param>
    /// <returns>区間全体に信号があるとき true。</returns>
    public static bool IsSteady(ReadOnlySpan<double> pcm)
    {
        if (pcm.Length < SteadySegments)
        {
            return false;
        }

        var mean = Mean(pcm);
        var totalMs = MeanSquare(pcm, mean);
        var silenceMs = SilenceRms * SilenceRms;
        if (totalMs < silenceMs)
        {
            return false;
        }

        var minMs = Math.Max(silenceMs, totalMs * SteadyMinRatio * SteadyMinRatio);
        var segmentLength = pcm.Length / SteadySegments;
        for (var s = 0; s < SteadySegments; s++)
        {
            if (MeanSquare(pcm.Slice(s * segmentLength, segmentLength), mean) < minMs)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 平均値を返します。
    /// </summary>
    /// <param name="pcm">PCM（空でない）。</param>
    /// <returns>平均値。</returns>
    private static double Mean(ReadOnlySpan<double> pcm)
    {
        var sum = 0.0;
        foreach (var v in pcm)
        {
            sum += v;
        }

        return sum / pcm.Length;
    }

    /// <summary>
    /// 指定の平均値を引いた二乗平均を返します。
    /// </summary>
    /// <param name="pcm">PCM（空でない）。</param>
    /// <param name="mean">差し引く平均値。</param>
    /// <returns>二乗平均。</returns>
    private static double MeanSquare(ReadOnlySpan<double> pcm, double mean)
    {
        var energy = 0.0;
        foreach (var v in pcm)
        {
            var d = v - mean;
            energy += d * d;
        }

        return energy / pcm.Length;
    }
}
