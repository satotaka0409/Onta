namespace Onta.View.Performance;

/// <summary>
/// オシロスコープの表示窓です（AUTO 起立トリガー結果）。
/// </summary>
public readonly struct OscilloscopeCapture
{
    /// <summary>
    /// 表示窓を初期化します。
    /// </summary>
    public OscilloscopeCapture(int start, int length, int triggerOffset, bool triggered, double level)
    {
        Start = start;
        Length = length;
        TriggerOffset = triggerOffset;
        Triggered = triggered;
        Level = level;
    }

    /// <summary>入力バッファ上の開始インデックス。</summary>
    public int Start { get; }

    /// <summary>表示サンプル数。</summary>
    public int Length { get; }

    /// <summary>表示窓内のトリガー位置（起立点）。未検出時は 0。</summary>
    public int TriggerOffset { get; }

    /// <summary>起立エッジを検出できたか。</summary>
    public bool Triggered { get; }

    /// <summary>自動トリガーレベル（振幅）。</summary>
    public double Level { get; }
}

/// <summary>
/// 性能測定オシロの AUTO 起立トリガーです。
/// レベル・時間軸・プリトリガーは信号から自動で決めます。
/// </summary>
public static class OscilloscopeTrigger
{
    /// <summary>表示窓の先頭側に置くプリトリガー比率です。</summary>
    public const double PreTriggerRatio = 0.2;

    /// <summary>推定周期あたりの表示波数です。</summary>
    public const int TargetCycles = 6;

    /// <summary>表示の最短サンプル数（約 5 ms @ 44.1 kHz）。</summary>
    public const int MinDisplaySamples = 220;

    /// <summary>周期が取れないときの既定表示長（20 ms @ 44.1 kHz）。</summary>
    public const int DefaultDisplaySamples = 882;

    /// <summary>これ未満の峰は無信号とみなします。</summary>
    public const double MinPeak = 0.03;

    /// <summary>
    /// AUTO 起立で表示窓を切り出します。見つからなければ末尾窓（フリーラン）です。
    /// </summary>
    /// <param name="samples">直近 PCM（振幅 -1..1）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <returns>表示開始・長さ・トリガー位置。</returns>
    public static OscilloscopeCapture Find(ReadOnlySpan<double> samples, int sampleRate) =>
        Find(samples, sampleRate, displaySamples: 0);

    /// <summary>
    /// AUTO 起立で表示窓を切り出します。<paramref name="displaySamples"/> が正なら時間軸は指定長です。
    /// </summary>
    /// <param name="samples">直近 PCM（振幅 -1..1）。</param>
    /// <param name="sampleRate">サンプリング周波数。</param>
    /// <param name="displaySamples">表示サンプル数。0 以下なら周期から自動。</param>
    /// <returns>表示開始・長さ・トリガー位置。</returns>
    public static OscilloscopeCapture Find(ReadOnlySpan<double> samples, int sampleRate, int displaySamples)
    {
        var n = samples.Length;
        if (n < 8 || sampleRate <= 0)
        {
            return new OscilloscopeCapture(0, Math.Max(0, n), 0, false, 0.0);
        }

        Measure(samples, out var min, out var max);
        var peakToPeak = max - min;
        var peak = Math.Max(Math.Abs(min), Math.Abs(max));
        var requested = displaySamples > 0
            ? Math.Clamp(displaySamples, 16, n)
            : 0;
        if (peak < MinPeak || peakToPeak < MinPeak)
        {
            var idleLen = requested > 0 ? requested : Math.Min(n, DefaultDisplaySamples);
            return new OscilloscopeCapture(n - idleLen, idleLen, 0, false, 0.0);
        }

        var level = 0.5 * (min + max);
        var hyst = Math.Max(0.012, 0.12 * peakToPeak * 0.5);
        int display;
        if (requested > 0)
        {
            display = requested;
        }
        else
        {
            var period = EstimatePeriodSamples(samples, level, hyst);
            display = period >= 8
                ? period * TargetCycles
                : DefaultDisplaySamples;
            display = Math.Clamp(display, MinDisplaySamples, n);
        }

        var pre = Math.Clamp((int)Math.Round(display * PreTriggerRatio), 1, Math.Max(2, display - 2));
        var post = display - pre;
        var trigger = FindLatestRising(samples, level, hyst, minIndex: pre, maxIndexInclusive: n - post);
        if (trigger >= 0)
        {
            return new OscilloscopeCapture(trigger - pre, display, pre, true, level);
        }

        return new OscilloscopeCapture(n - display, display, 0, false, level);
    }

    /// <summary>
    /// 振幅の最小・最大を取ります。
    /// </summary>
    private static void Measure(ReadOnlySpan<double> samples, out double min, out double max)
    {
        min = double.PositiveInfinity;
        max = double.NegativeInfinity;
        for (var i = 0; i < samples.Length; i++)
        {
            var v = samples[i];
            if (v < min)
            {
                min = v;
            }

            if (v > max)
            {
                max = v;
            }
        }
    }

    /// <summary>
    /// 直近の起立間隔の中央値から周期を推定します。ばらつきが大きいと 0 です。
    /// </summary>
    private static int EstimatePeriodSamples(ReadOnlySpan<double> samples, double level, double hyst)
    {
        Span<int> last = stackalloc int[8];
        var count = 0;
        var write = 0;
        var prev = -1;
        var armed = false;
        var lo = level - hyst;
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i] < lo)
            {
                armed = true;
            }

            if (!armed || samples[i] < level || samples[i - 1] >= level)
            {
                continue;
            }

            if (prev >= 0)
            {
                var d = i - prev;
                if (d >= 2)
                {
                    last[write & 7] = d;
                    write++;
                    if (count < 8)
                    {
                        count++;
                    }
                }
            }

            prev = i;
            armed = false;
        }

        if (count < 2)
        {
            return 0;
        }

        Span<int> used = stackalloc int[count];
        if (count < 8)
        {
            last[..count].CopyTo(used);
        }
        else
        {
            for (var i = 0; i < 8; i++)
            {
                used[i] = last[(write - 8 + i) & 7];
            }
        }

        InsertionSort(used);
        if (used[count - 1] > used[0] * 2)
        {
            return 0;
        }

        return used[count / 2];
    }

    /// <summary>
    /// プリ／ポストを確保できる範囲で、最も新しい起立エッジを探します。
    /// </summary>
    private static int FindLatestRising(
        ReadOnlySpan<double> samples,
        double level,
        double hyst,
        int minIndex,
        int maxIndexInclusive)
    {
        if (maxIndexInclusive < minIndex)
        {
            return -1;
        }

        var armed = false;
        var last = -1;
        var lo = level - hyst;
        var end = Math.Min(samples.Length - 1, maxIndexInclusive);
        for (var i = 1; i <= end; i++)
        {
            if (samples[i] < lo)
            {
                armed = true;
            }

            if (!armed || samples[i] < level || samples[i - 1] >= level)
            {
                continue;
            }

            if (i >= minIndex)
            {
                last = i;
            }

            armed = false;
        }

        return last;
    }

    /// <summary>
    /// 小さな整数列を昇順にします。
    /// </summary>
    private static void InsertionSort(Span<int> values)
    {
        for (var i = 1; i < values.Length; i++)
        {
            var key = values[i];
            var j = i - 1;
            while (j >= 0 && values[j] > key)
            {
                values[j + 1] = values[j];
                j--;
            }

            values[j + 1] = key;
        }
    }
}
