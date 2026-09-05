using System.Numerics;

namespace Onta.Core;

/// <summary>
/// カセット相当のワウ・フラッター速度変調と、可逆な付与／逆補正を提供します。
/// 速度モデルは NoisePlus と同じ:
/// speed = 1 + A*(0.65*sin(2π·0.5·t+φw) + 0.35*sin(2π·6·t+φf))（下限 0.05）。
/// 付与は最近傍インデックス写像、逆補正は同じ写像の gather なので、量子化が無ければ残差は穴埋め部のみです。
/// </summary>
public static class WowFlutterWarp
{
    public const double WowFrequencyHz = 0.5;
    public const double FlutterFrequencyHz = 6.0;
    public const int DefaultOversample = 8;

    /// <summary>
    /// シードから wow / flutter 位相を生成します（NoisePlus と同じ順序）。
    /// </summary>
    public static (double WowPhase, double FlutterPhase) CreatePhases(int seed)
    {
        var random = seed == 0 ? new Random() : new Random(seed);
        var wowPhase = random.NextDouble() * 2.0 * Math.PI;
        var flutterPhase = random.NextDouble() * 2.0 * Math.PI;
        return (wowPhase, flutterPhase);
    }

    /// <summary>
    /// サンプル単位の速度プロファイルを構築します。
    /// </summary>
    public static double[] BuildSpeedProfile(
        int sampleCount,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        var profile = new double[sampleCount];
        var sr = Math.Max(1, sampleRate);
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sr;
            var modulation =
                (0.65 * Math.Sin((2.0 * Math.PI * WowFrequencyHz * t) + wowPhase)) +
                (0.35 * Math.Sin((2.0 * Math.PI * FlutterFrequencyHz * t) + flutterPhase));
            var speed = 1.0 + (amount * modulation);
            profile[i] = speed < 0.05 ? 0.05 : speed;
        }

        return profile;
    }

    /// <summary>
    /// 終端正規化付き累積と、出力→入力の最近傍インデックス写像を構築します。
    /// 写像は全ソース添え字を少なくとも1回含むよう補正し、gather 逆変換が穴なしで復元できるようにします。
    /// </summary>
    public static int[] BuildSourceIndexMap(
        int sampleCount,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        if (sampleCount <= 0)
        {
            return [];
        }

        var profile = BuildSpeedProfile(sampleCount, sampleRate, amount, wowPhase, flutterPhase);
        BuildCumul(profile, out var cumul, out var scale);
        var map = new int[sampleCount];
        var last = sampleCount - 1;
        var count = new int[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            var srcPos = cumul[i] * scale;
            var idx = (int)Math.Round(srcPos, MidpointRounding.AwayFromZero);
            map[i] = Math.Clamp(idx, 0, last);
            count[map[i]]++;
        }

        // 未参照ソースを、重複参照している出力スロットへ再割当て（全射化）。
        // missing / donor を位置順に突き合わせ、O(n log n) で穴を埋める。
        var missing = new List<int>();
        var donors = new List<int>();
        for (var j = 0; j < sampleCount; j++)
        {
            if (count[j] == 0)
            {
                missing.Add(j);
            }
        }

        for (var i = 0; i < sampleCount; i++)
        {
            if (count[map[i]] > 1)
            {
                donors.Add(i);
            }
        }

        donors.Sort((a, b) => (cumul[a] * scale).CompareTo(cumul[b] * scale));
        missing.Sort();

        var donorCursor = 0;
        for (var m = 0; m < missing.Count; m++)
        {
            var target = missing[m];
            while (donorCursor < donors.Count && count[map[donors[donorCursor]]] <= 1)
            {
                donorCursor++;
            }

            var bestI = -1;
            if (donorCursor < donors.Count)
            {
                bestI = donors[donorCursor];
                // 近傍の donor を少し先読みして、位置が近い方を選ぶ
                var bestScore = Math.Abs((cumul[bestI] * scale) - target);
                for (var k = donorCursor + 1; k < donors.Count && k < donorCursor + 8; k++)
                {
                    if (count[map[donors[k]]] <= 1)
                    {
                        continue;
                    }

                    var score = Math.Abs((cumul[donors[k]] * scale) - target);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestI = donors[k];
                    }
                }
            }
            else
            {
                // donor が尽きた場合は最も近い出力を強制上書き
                var bestScore = double.PositiveInfinity;
                for (var i = 0; i < sampleCount; i++)
                {
                    var score = Math.Abs((cumul[i] * scale) - target);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestI = i;
                    }
                }
            }

            if (bestI < 0)
            {
                continue;
            }

            count[map[bestI]]--;
            map[bestI] = target;
            count[target]++;
        }

        return map;
    }

    /// <summary>
    /// 可逆ワウ付与（最近傍写像）。double 精度の実信号向けです。
    /// </summary>
    public static double[] Apply(
        ReadOnlySpan<double> source,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        if (source.Length == 0 || amount == 0.0)
        {
            return source.ToArray();
        }

        var map = BuildSourceIndexMap(source.Length, sampleRate, amount, wowPhase, flutterPhase);
        var dst = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            dst[i] = source[map[i]];
        }

        return dst;
    }

    /// <summary>
    /// 可逆ワウ逆補正（同一写像の gather + 穴の線形埋め）。
    /// </summary>
    public static double[] Correct(
        ReadOnlySpan<double> warped,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        if (warped.Length == 0 || amount == 0.0)
        {
            return warped.ToArray();
        }

        var n = warped.Length;
        var map = BuildSourceIndexMap(n, sampleRate, amount, wowPhase, flutterPhase);
        var sum = new double[n];
        var count = new int[n];
        for (var i = 0; i < n; i++)
        {
            var src = map[i];
            sum[src] += warped[i];
            count[src]++;
        }

        var corrected = new double[n];
        for (var j = 0; j < n; j++)
        {
            // 全射写像なので count[j] >= 1 が保証される。
            corrected[j] = count[j] > 0 ? sum[j] / count[j] : 0.0;
        }

        return corrected;
    }

    /// <summary>
    /// 複素 OFDM 実信号（Imag≈0）へ可逆ワウを付与します。
    /// </summary>
    public static Complex[] Apply(
        Complex[] source,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0 || amount == 0.0)
        {
            return (Complex[])source.Clone();
        }

        var real = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            real[i] = source[i].Real;
        }

        var warped = Apply(real, sampleRate, amount, wowPhase, flutterPhase);
        var dst = new Complex[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            dst[i] = new Complex(warped[i], 0.0);
        }

        return dst;
    }

    /// <summary>
    /// 複素 OFDM 実信号の可逆ワウ逆補正です。
    /// </summary>
    public static Complex[] Correct(
        Complex[] warped,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        ArgumentNullException.ThrowIfNull(warped);
        if (warped.Length == 0 || amount == 0.0)
        {
            return (Complex[])warped.Clone();
        }

        var real = new double[warped.Length];
        for (var i = 0; i < warped.Length; i++)
        {
            real[i] = warped[i].Real;
        }

        var corrected = Correct(real, sampleRate, amount, wowPhase, flutterPhase);
        var dst = new Complex[warped.Length];
        for (var i = 0; i < warped.Length; i++)
        {
            dst[i] = new Complex(corrected[i], 0.0);
        }

        return dst;
    }

    /// <summary>
    /// 線形補間＋ソース側オーバーサンプルによる低残差ワウ付与です。
    /// 出力長は入力と同じで、読み出しだけを高分解能化します。
    /// </summary>
    public static double[] ApplyOversampledLinear(
        ReadOnlySpan<double> source,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        int oversample = DefaultOversample)
    {
        if (source.Length == 0 || amount == 0.0)
        {
            return source.ToArray();
        }

        oversample = Math.Max(1, oversample);
        var n = source.Length;
        var up = oversample == 1 ? source.ToArray() : UpsampleLinear(source, oversample);
        var profile = BuildSpeedProfile(n, sampleRate, amount, wowPhase, flutterPhase);
        BuildCumul(profile, out var cumul, out var scale);
        var dst = new double[n];
        for (var i = 0; i < n; i++)
        {
            dst[i] = SampleLinear(up, cumul[i] * scale * oversample);
        }

        return dst;
    }

    /// <summary>
    /// 線形補間＋ワープ側オーバーサンプルによる低残差ワウ逆補正です。
    /// </summary>
    public static double[] CorrectOversampledLinear(
        ReadOnlySpan<double> warped,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        int oversample = DefaultOversample)
    {
        if (warped.Length == 0 || amount == 0.0)
        {
            return warped.ToArray();
        }

        oversample = Math.Max(1, oversample);
        var n = warped.Length;
        var up = oversample == 1 ? warped.ToArray() : UpsampleLinear(warped, oversample);
        var profile = BuildSpeedProfile(n, sampleRate, amount, wowPhase, flutterPhase);
        BuildCumul(profile, out var cumul, out var scale);
        var dst = new double[n];
        var warpedIndex = 0;
        for (var j = 0; j < n; j++)
        {
            var target = j / scale;
            while (warpedIndex < n - 1 && cumul[warpedIndex + 1] < target)
            {
                warpedIndex++;
            }

            if (warpedIndex >= n - 1)
            {
                dst[j] = SampleLinear(up, (n - 1) * (double)oversample);
                continue;
            }

            var span = cumul[warpedIndex + 1] - cumul[warpedIndex];
            var frac = span > 1e-12 ? (target - cumul[warpedIndex]) / span : 0.0;
            frac = Math.Clamp(frac, 0.0, 1.0);
            dst[j] = SampleLinear(up, (warpedIndex + frac) * oversample);
        }

        return dst;
    }

    public static Complex[] ApplyOversampledLinear(
        Complex[] source,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        int oversample = DefaultOversample)
    {
        ArgumentNullException.ThrowIfNull(source);
        var real = new double[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            real[i] = source[i].Real;
        }

        var warped = ApplyOversampledLinear(real, sampleRate, amount, wowPhase, flutterPhase, oversample);
        var dst = new Complex[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            dst[i] = new Complex(warped[i], 0.0);
        }

        return dst;
    }

    public static Complex[] CorrectOversampledLinear(
        Complex[] warped,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        int oversample = DefaultOversample)
    {
        ArgumentNullException.ThrowIfNull(warped);
        var real = new double[warped.Length];
        for (var i = 0; i < warped.Length; i++)
        {
            real[i] = warped[i].Real;
        }

        var corrected = CorrectOversampledLinear(real, sampleRate, amount, wowPhase, flutterPhase, oversample);
        var dst = new Complex[warped.Length];
        for (var i = 0; i < warped.Length; i++)
        {
            dst[i] = new Complex(corrected[i], 0.0);
        }

        return dst;
    }

    private static void BuildCumul(double[] speedProfile, out double[] cumul, out double scale)
    {
        var n = speedProfile.Length;
        cumul = new double[n];
        if (n == 0)
        {
            scale = 1.0;
            return;
        }

        cumul[0] = 0.0;
        for (var i = 0; i < n - 1; i++)
        {
            var speed = speedProfile[i];
            if (speed < 1e-6)
            {
                speed = 1.0;
            }

            cumul[i + 1] = cumul[i] + speed;
        }

        scale = cumul[^1] > 1e-12 ? (n - 1) / cumul[^1] : 1.0;
    }

    private static double[] UpsampleLinear(ReadOnlySpan<double> source, int factor)
    {
        if (source.Length == 0)
        {
            return [];
        }

        var nUp = ((source.Length - 1) * factor) + 1;
        var up = new double[nUp];
        for (var k = 0; k < nUp; k++)
        {
            up[k] = SampleLinear(source, k / (double)factor);
        }

        return up;
    }

    private static double SampleLinear(ReadOnlySpan<double> samples, double position)
    {
        if (samples.Length == 0)
        {
            return 0.0;
        }

        if (position <= 0.0)
        {
            return samples[0];
        }

        if (position >= samples.Length - 1)
        {
            return samples[^1];
        }

        var index = (int)position;
        var frac = position - index;
        var a = samples[index];
        var b = samples[index + 1];
        return a + ((b - a) * frac);
    }
}

