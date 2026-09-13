using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Onta.Core;

/// <summary>
/// ワウ・フラッター速度変調の付与と逆補正を行うユーティリティです。
/// </summary>
public static class WowFlutterWarp
{
    public const double WowFrequencyHz = 0.5;
    public const double FlutterFrequencyHz = 6.0;
    public const int DefaultOversample = 8;

    /// <summary>
    /// シード値から wow/flutter の初期位相を生成します。
    /// </summary>
    /// <param name="seed">乱数シード。0 の場合は非固定シード。</param>
    /// <returns>wow 位相と flutter 位相（ラジアン）。</returns>
    public static (double WowPhase, double FlutterPhase) CreatePhases(int seed)
    {
        var random = seed == 0 ? new Random() : new Random(seed);
        var wowPhase = random.NextDouble() * 2.0 * Math.PI;
        var flutterPhase = random.NextDouble() * 2.0 * Math.PI;
        return (wowPhase, flutterPhase);
    }

    /// <summary>
    /// 速度変調プロファイルを生成します。
    /// </summary>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <returns>サンプルごとの相対速度。</returns>
    public static double[] BuildSpeedProfile(
        int sampleCount,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        var profile = new double[sampleCount];
        FillSpeedProfile(profile, sampleRate, amount, wowPhase, flutterPhase);
        return profile;
    }

    /// <summary>
    /// 速度変調プロファイルを既存バッファへ書き込みます。
    /// </summary>
    /// <param name="profile">profile を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    private static void FillSpeedProfile(
        Span<double> profile,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        var sr = Math.Max(1, sampleRate);
        var wowStep = 2.0 * Math.PI * WowFrequencyHz / sr;
        var flutterStep = 2.0 * Math.PI * FlutterFrequencyHz / sr;

        // 複素回転で sin を進め、512 サンプルごとに解析角へ再同期（長尺の 2×Sin/サンプルを回避）。
        var cosWowStep = Math.Cos(wowStep);
        var sinWowStep = Math.Sin(wowStep);
        var cosFlutterStep = Math.Cos(flutterStep);
        var sinFlutterStep = Math.Sin(flutterStep);
        var wowSin = Math.Sin(wowPhase);
        var wowCos = Math.Cos(wowPhase);
        var flutterSin = Math.Sin(flutterPhase);
        var flutterCos = Math.Cos(flutterPhase);
        const int reanchorEvery = 512;
        var untilReanchor = reanchorEvery;

        for (var i = 0; i < profile.Length; i++)
        {
            var modulation = (0.65 * wowSin) + (0.35 * flutterSin);
            var speed = 1.0 + (amount * modulation);
            profile[i] = speed < 0.05 ? 0.05 : speed;

            var nextWowSin = (wowSin * cosWowStep) + (wowCos * sinWowStep);
            var nextWowCos = (wowCos * cosWowStep) - (wowSin * sinWowStep);
            wowSin = nextWowSin;
            wowCos = nextWowCos;

            var nextFlutterSin = (flutterSin * cosFlutterStep) + (flutterCos * sinFlutterStep);
            var nextFlutterCos = (flutterCos * cosFlutterStep) - (flutterSin * sinFlutterStep);
            flutterSin = nextFlutterSin;
            flutterCos = nextFlutterCos;

            if (--untilReanchor == 0)
            {
                untilReanchor = reanchorEvery;
                var wowAngle = wowPhase + ((i + 1) * wowStep);
                var flutterAngle = flutterPhase + ((i + 1) * flutterStep);
                wowSin = Math.Sin(wowAngle);
                wowCos = Math.Cos(wowAngle);
                flutterSin = Math.Sin(flutterAngle);
                flutterCos = Math.Cos(flutterAngle);
            }
        }
    }

    /// <summary>
    /// 出力サンプルごとに参照する入力インデックス表を生成します。
    /// </summary>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <returns>入力インデックス表。</returns>
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

        sampleRate = Math.Max(1, sampleRate);
        var wowOmega = 2.0 * Math.PI * WowFrequencyHz / sampleRate;
        var flutterOmega = 2.0 * Math.PI * FlutterFrequencyHz / sampleRate;
        var wowGain = amount * 0.65;
        var flutterGain = amount * 0.35;

        // SumOfSines と同じ解析累積。長尺は速度漸化＋512 サンプル再同期で近似し、
        // 再同期点では CumulAt で累積を解析値へ戻す。
        double CumulAt(int i)
        {
            if (i <= 0)
            {
                return 0.0;
            }

            return i
                + (wowGain * SumOfSines(wowPhase, wowOmega, i))
                + (flutterGain * SumOfSines(flutterPhase, flutterOmega, i));
        }

        var count = ArrayPool<int>.Shared.Rent(sampleCount);
        var cumul = ArrayPool<double>.Shared.Rent(sampleCount);
        try
        {
            var last = sampleCount - 1;
            var cumulEnd = CumulAt(last);
            var scale = cumulEnd > 1e-12 ? last / cumulEnd : 1.0;
            var map = new int[sampleCount];
            Array.Clear(count, 0, sampleCount);

            var cosWowStep = Math.Cos(wowOmega);
            var sinWowStep = Math.Sin(wowOmega);
            var cosFlutterStep = Math.Cos(flutterOmega);
            var sinFlutterStep = Math.Sin(flutterOmega);
            var wowSin = Math.Sin(wowPhase);
            var wowCos = Math.Cos(wowPhase);
            var flutterSin = Math.Sin(flutterPhase);
            var flutterCos = Math.Cos(flutterPhase);
            var c = 0.0;
            const int reanchorEvery = 512;
            var untilReanchor = reanchorEvery;

            for (var i = 0; i < sampleCount; i++)
            {
                cumul[i] = c;
                var idx = RoundAwayFromZeroPositive(c * scale);
                if ((uint)idx > (uint)last)
                {
                    idx = idx < 0 ? 0 : last;
                }

                map[i] = idx;
                count[idx]++;

                c += 1.0 + (wowGain * wowSin) + (flutterGain * flutterSin);

                var nextWowSin = (wowSin * cosWowStep) + (wowCos * sinWowStep);
                var nextWowCos = (wowCos * cosWowStep) - (wowSin * sinWowStep);
                wowSin = nextWowSin;
                wowCos = nextWowCos;

                var nextFlutterSin = (flutterSin * cosFlutterStep) + (flutterCos * sinFlutterStep);
                var nextFlutterCos = (flutterCos * cosFlutterStep) - (flutterSin * sinFlutterStep);
                flutterSin = nextFlutterSin;
                flutterCos = nextFlutterCos;

                if (--untilReanchor == 0)
                {
                    untilReanchor = reanchorEvery;
                    var next = i + 1;
                    if (next < sampleCount)
                    {
                        c = CumulAt(next);
                        var wowAngle = wowPhase + (next * wowOmega);
                        var flutterAngle = flutterPhase + (next * flutterOmega);
                        wowSin = Math.Sin(wowAngle);
                        wowCos = Math.Cos(wowAngle);
                        flutterSin = Math.Sin(flutterAngle);
                        flutterCos = Math.Cos(flutterAngle);
                    }
                }
            }

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
                    // donors が尽きた場合の O(n) 全走査は長尺で固まるので最近傍へ丸める
                    var approx = RoundAwayFromZeroPositive(target / Math.Max(scale, 1e-12));
                    bestI = Math.Clamp(approx, 0, sampleCount - 1);
                    if (count[map[bestI]] <= 1)
                    {
                        var left = bestI;
                        var right = bestI;
                        while (left > 0 || right < sampleCount - 1)
                        {
                            if (left > 0)
                            {
                                left--;
                                if (count[map[left]] > 1)
                                {
                                    bestI = left;
                                    break;
                                }
                            }

                            if (right < sampleCount - 1)
                            {
                                right++;
                                if (count[map[right]] > 1)
                                {
                                    bestI = right;
                                    break;
                                }
                            }
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
        finally
        {
            ArrayPool<int>.Shared.Return(count);
            ArrayPool<double>.Shared.Return(cumul);
        }
    }

    /// <summary>
    /// 実数波形へ wow/flutter 変調を付与します。
    /// </summary>
    /// <param name="source">入力波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <returns>変調後波形。</returns>
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
    /// wow/flutter 変調済み実数波形を逆補正します。
    /// </summary>
    /// <param name="warped">変調済み波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <returns>補正後波形。</returns>
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
        var sum = ArrayPool<double>.Shared.Rent(n);
        var count = ArrayPool<int>.Shared.Rent(n);
        try
        {
            Array.Clear(sum, 0, n);
            Array.Clear(count, 0, n);
            for (var i = 0; i < n; i++)
            {
                var src = map[i];
                sum[src] += warped[i];
                count[src]++;
            }

            var corrected = new double[n];
            for (var j = 0; j < n; j++)
            {
                corrected[j] = sum[j] / count[j];
            }

            return corrected;
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sum);
            ArrayPool<int>.Shared.Return(count);
        }
    }

    /// <summary>
    /// 複素波形へ wow/flutter 変調を付与します（実部を使用）。
    /// </summary>
    /// <param name="source">入力複素波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <returns>変調後複素波形。</returns>
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

        var map = BuildSourceIndexMap(source.Length, sampleRate, amount, wowPhase, flutterPhase);
        var dst = new Complex[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            dst[i] = new Complex(source[map[i]].Real, 0.0);
        }

        return dst;
    }

    /// <summary>
    /// 複素波形の wow/flutter 変調を逆補正します。
    /// </summary>
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <returns>補正後複素波形。</returns>
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

        var dst = new Complex[warped.Length];
        CorrectInto(warped, sampleRate, amount, wowPhase, flutterPhase, dst);
        return dst;
    }

    /// <summary>
    /// 複素波形をインプレースで逆補正します。
    /// </summary>
    /// <param name="warped">補正対象の複素波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    public static void CorrectInPlace(
        Complex[] warped,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        ArgumentNullException.ThrowIfNull(warped);
        CorrectInPlace(warped, warped.Length, sampleRate, amount, wowPhase, flutterPhase);
    }

    /// <summary>
    /// 全波形長基準の scale で、指定プレフィックス区間だけを散乱 Correct して destination へ書きます。
    /// Refine 採点用（先頭切り出し Correct の scale ずれを避けつつ全波形 Correct より軽量）。
    /// </summary>
    public static void CorrectPrefixWithReferenceLength(
        Complex[] warped,
        int referenceLength,
        int prefixStart,
        int prefixLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        Span<Complex> destination)
    {
        ArgumentNullException.ThrowIfNull(warped);
        if (prefixLength <= 0)
        {
            return;
        }

        if (destination.Length < prefixLength)
        {
            throw new ArgumentException("Destination is shorter than prefix length.", nameof(destination));
        }

        if (amount == 0.0)
        {
            warped.AsSpan(prefixStart, prefixLength).CopyTo(destination);
            return;
        }

        var sum = ArrayPool<double>.Shared.Rent(prefixLength);
        var count = ArrayPool<int>.Shared.Rent(prefixLength);
        try
        {
            ScatterCorrectPrefix(
                warped,
                referenceLength,
                prefixStart,
                prefixLength,
                sampleRate,
                amount,
                wowPhase,
                flutterPhase,
                sum.AsSpan(0, prefixLength),
                count.AsSpan(0, prefixLength));

            for (var j = 0; j < prefixLength; j++)
            {
                destination[j] = count[j] > 0
                    ? new Complex(sum[j] / count[j], 0.0)
                    : warped[Math.Clamp(prefixStart + j, 0, warped.Length - 1)];
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sum);
            ArrayPool<int>.Shared.Return(count);
        }
    }

    /// <summary>
    /// CorrectPrefix と同じ散乱補正を行い、補正後実部と ideal の正規化相関を返します。
    /// MatchWow / Refine の大量採点向け（Complex 書き出しと ArrayPool 毎回 Rent を省略）。
    /// </summary>
    public static double CorrectPrefixCorrelateReal(
        Complex[] warped,
        int referenceLength,
        int prefixStart,
        int prefixLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        ReadOnlySpan<double> idealReals,
        double meanIdeal,
        double energyIdeal,
        Span<double> sumScratch,
        Span<int> countScratch)
    {
        ArgumentNullException.ThrowIfNull(warped);
        if (prefixLength <= 0 || idealReals.Length < prefixLength)
        {
            return double.NegativeInfinity;
        }

        if (sumScratch.Length < prefixLength || countScratch.Length < prefixLength)
        {
            throw new ArgumentException("Scratch buffers are shorter than prefix length.");
        }

        if (amount == 0.0)
        {
            for (var j = 0; j < prefixLength; j++)
            {
                sumScratch[j] = warped[Math.Clamp(prefixStart + j, 0, warped.Length - 1)].Real;
            }

            return CorrelateDenseReals(sumScratch[..prefixLength], idealReals[..prefixLength], meanIdeal, energyIdeal);
        }

        ScatterCorrectPrefix(
            warped,
            referenceLength,
            prefixStart,
            prefixLength,
            sampleRate,
            amount,
            wowPhase,
            flutterPhase,
            sumScratch[..prefixLength],
            countScratch[..prefixLength]);

        for (var j = 0; j < prefixLength; j++)
        {
            sumScratch[j] = countScratch[j] > 0
                ? sumScratch[j] / countScratch[j]
                : warped[Math.Clamp(prefixStart + j, 0, warped.Length - 1)].Real;
        }

        return CorrelateDenseReals(sumScratch[..prefixLength], idealReals[..prefixLength], meanIdeal, energyIdeal);
    }

    /// <summary>
    /// 散乱 Correct の sum/count を埋めます（呼び出し側で Clear 済み scratch を渡す必要なし）。
    /// </summary>
    private static void ScatterCorrectPrefix(
        Complex[] warped,
        int referenceLength,
        int prefixStart,
        int prefixLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        Span<double> sum,
        Span<int> count)
    {
        referenceLength = Math.Max(referenceLength, prefixStart + prefixLength);
        referenceLength = Math.Max(referenceLength, 2);
        sampleRate = Math.Max(1, sampleRate);

        var wowOmega = 2.0 * Math.PI * WowFrequencyHz / sampleRate;
        var flutterOmega = 2.0 * Math.PI * FlutterFrequencyHz / sampleRate;
        var wowGain = amount * 0.65;
        var flutterGain = amount * 0.35;

        double CumulAt(int i)
        {
            if (i <= 0)
            {
                return 0.0;
            }

            return i
                + (wowGain * SumOfSines(wowPhase, wowOmega, i))
                + (flutterGain * SumOfSines(flutterPhase, flutterOmega, i));
        }

        var absEnd = referenceLength - 1;
        var cumulEnd = CumulAt(absEnd);
        var scale = cumulEnd > 1e-12 ? absEnd / cumulEnd : 1.0;
        var prefixEnd = prefixStart + prefixLength;

        // amount≈0.01 では map[i]≈i。プレフィックスへ寄与する i は近傍に閉じる。
        var slack = Math.Max(sampleRate / 5, (int)(prefixLength * 0.05) + 64);
        var iMin = Math.Max(0, prefixStart - slack);
        var iMax = Math.Min(warped.Length, prefixEnd + slack);

        sum.Clear();
        count.Clear();

        // SumOfSines 毎サンプルより speed 漸化が安い。さらに Math.Sin 連打を避け、
        // 複素回転で sin を進め、512 サンプルごとに解析角へ再同期してドリフトを抑える。
        var cumul = CumulAt(iMin);
        var cosWowStep = Math.Cos(wowOmega);
        var sinWowStep = Math.Sin(wowOmega);
        var cosFlutterStep = Math.Cos(flutterOmega);
        var sinFlutterStep = Math.Sin(flutterOmega);
        var wowAngle = wowPhase + (iMin * wowOmega);
        var flutterAngle = flutterPhase + (iMin * flutterOmega);
        var wowSin = Math.Sin(wowAngle);
        var wowCos = Math.Cos(wowAngle);
        var flutterSin = Math.Sin(flutterAngle);
        var flutterCos = Math.Cos(flutterAngle);
        const int reanchorEvery = 512;
        var untilReanchor = reanchorEvery;

        for (var i = iMin; i < iMax; i++)
        {
            var src = RoundAwayFromZeroPositive(cumul * scale);
            if ((uint)(src - prefixStart) < (uint)prefixLength)
            {
                var local = src - prefixStart;
                sum[local] += warped[i].Real;
                count[local]++;
            }

            cumul += 1.0 + (wowGain * wowSin) + (flutterGain * flutterSin);

            var nextWowSin = (wowSin * cosWowStep) + (wowCos * sinWowStep);
            var nextWowCos = (wowCos * cosWowStep) - (wowSin * sinWowStep);
            wowSin = nextWowSin;
            wowCos = nextWowCos;

            var nextFlutterSin = (flutterSin * cosFlutterStep) + (flutterCos * sinFlutterStep);
            var nextFlutterCos = (flutterCos * cosFlutterStep) - (flutterSin * sinFlutterStep);
            flutterSin = nextFlutterSin;
            flutterCos = nextFlutterCos;

            if (--untilReanchor == 0)
            {
                untilReanchor = reanchorEvery;
                wowAngle = wowPhase + ((i + 1) * wowOmega);
                flutterAngle = flutterPhase + ((i + 1) * flutterOmega);
                wowSin = Math.Sin(wowAngle);
                wowCos = Math.Cos(wowAngle);
                flutterSin = Math.Sin(flutterAngle);
                flutterCos = Math.Cos(flutterAngle);
            }
        }
    }

    /// <summary>
    /// 連続 double 系列の正規化相関係数（ideal 側 mean/energy 既知）。
    /// </summary>
    public static double CorrelateDenseReals(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        double meanB,
        double energyB)
    {
        var count = Math.Min(a.Length, b.Length);
        if (count <= 1 || energyB <= 1e-18)
        {
            return double.NegativeInfinity;
        }

        if (Avx.IsSupported && count >= 8)
        {
            return CorrelateDenseRealsAvx(a, b, meanB, energyB, count);
        }

        if (AdvSimd.Arm64.IsSupported && count >= 4)
        {
            return CorrelateDenseRealsAdvSimd(a, b, meanB, energyB, count);
        }

        var sumA = 0.0;
        for (var i = 0; i < count; i++)
        {
            sumA += a[i];
        }

        var meanA = sumA / count;
        var num = 0.0;
        var energyA = 0.0;
        for (var i = 0; i < count; i++)
        {
            var xa = a[i] - meanA;
            var xb = b[i] - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    private static Vector256<double> LoadAvx(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector256<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    private static Vector128<double> LoadNeon(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector128<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    private static double CorrelateDenseRealsAvx(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        double meanB,
        double energyB,
        int count)
    {
        var sumVec = Vector256<double>.Zero;
        var i = 0;
        for (; i + 4 <= count; i += 4)
        {
            sumVec = Avx.Add(sumVec, LoadAvx(a, i));
        }

        var sumA = sumVec.GetElement(0) + sumVec.GetElement(1) + sumVec.GetElement(2) + sumVec.GetElement(3);
        for (; i < count; i++)
        {
            sumA += a[i];
        }

        var meanA = sumA / count;
        var meanAVec = Vector256.Create(meanA);
        var meanBVec = Vector256.Create(meanB);
        var numVec = Vector256<double>.Zero;
        var energyAVec = Vector256<double>.Zero;
        i = 0;
        for (; i + 4 <= count; i += 4)
        {
            var xa = Avx.Subtract(LoadAvx(a, i), meanAVec);
            var xb = Avx.Subtract(LoadAvx(b, i), meanBVec);
            numVec = Avx.Add(numVec, Avx.Multiply(xa, xb));
            energyAVec = Avx.Add(energyAVec, Avx.Multiply(xa, xa));
        }

        var num = numVec.GetElement(0) + numVec.GetElement(1) + numVec.GetElement(2) + numVec.GetElement(3);
        var energyA = energyAVec.GetElement(0) + energyAVec.GetElement(1)
            + energyAVec.GetElement(2) + energyAVec.GetElement(3);
        for (; i < count; i++)
        {
            var xa = a[i] - meanA;
            var xb = b[i] - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    private static double CorrelateDenseRealsAdvSimd(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        double meanB,
        double energyB,
        int count)
    {
        var sumVec = Vector128<double>.Zero;
        var i = 0;
        for (; i + 2 <= count; i += 2)
        {
            sumVec = AdvSimd.Arm64.Add(sumVec, LoadNeon(a, i));
        }

        var sumA = sumVec.GetElement(0) + sumVec.GetElement(1);
        for (; i < count; i++)
        {
            sumA += a[i];
        }

        var meanA = sumA / count;
        var meanAVec = Vector128.Create(meanA);
        var meanBVec = Vector128.Create(meanB);
        var numVec = Vector128<double>.Zero;
        var energyAVec = Vector128<double>.Zero;
        i = 0;
        for (; i + 2 <= count; i += 2)
        {
            var xa = AdvSimd.Arm64.Subtract(LoadNeon(a, i), meanAVec);
            var xb = AdvSimd.Arm64.Subtract(LoadNeon(b, i), meanBVec);
            numVec = AdvSimd.Arm64.Add(numVec, AdvSimd.Arm64.Multiply(xa, xb));
            energyAVec = AdvSimd.Arm64.Add(energyAVec, AdvSimd.Arm64.Multiply(xa, xa));
        }

        var num = numVec.GetElement(0) + numVec.GetElement(1);
        var energyA = energyAVec.GetElement(0) + energyAVec.GetElement(1);
        for (; i < count; i++)
        {
            var xa = a[i] - meanA;
            var xb = b[i] - meanB;
            num += xa * xb;
            energyA += xa * xa;
        }

        return num / Math.Sqrt((energyA * energyB) + 1e-18);
    }

    private static double SumOfSines(double phase0, double omega, int count)
    {
        if (count <= 0)
        {
            return 0.0;
        }

        var half = omega * 0.5;
        var denom = Math.Sin(half);
        if (Math.Abs(denom) < 1e-12)
        {
            return count * Math.Sin(phase0);
        }

        var numer = Math.Sin(count * half);
        return numer / denom * Math.Sin(phase0 + ((count - 1) * half));
    }

    /// <summary>
    /// 非負値向けの AwayFromZero 丸め（Math.Round より軽量）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundAwayFromZeroPositive(double value) =>
        (int)(value + 0.5);

    /// <summary>
    /// 複素波形の先頭指定長だけをインプレースで逆補正します。
    /// </summary>
    /// <param name="warped">補正対象の複素波形。</param>
    /// <param name="length">補正対象サンプル長。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    public static void CorrectInPlace(
        Complex[] warped,
        int length,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        ArgumentNullException.ThrowIfNull(warped);
        if (length <= 0 || amount == 0.0)
        {
            return;
        }

        if (length > warped.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        CorrectInto(warped, length, sampleRate, amount, wowPhase, flutterPhase, warped);
    }

    /// <summary>
    /// 逆補正結果を指定バッファへ書き込みます。
    /// </summary>
    /// <param name="warped">warped を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <param name="destination">destination を指定します。</param>
    private static void CorrectInto(
        Complex[] warped,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        Complex[] destination)
    {
        CorrectInto(warped, warped.Length, sampleRate, amount, wowPhase, flutterPhase, destination);
    }

    /// <summary>
    /// 先頭指定長の逆補正結果を指定バッファへ書き込みます。
    /// </summary>
    /// <param name="warped">warped を指定します。</param>
    /// <param name="length">length を指定します。</param>
    /// <param name="sampleRate">sampleRate を指定します。</param>
    /// <param name="amount">amount を指定します。</param>
    /// <param name="wowPhase">wowPhase を指定します。</param>
    /// <param name="flutterPhase">flutterPhase を指定します。</param>
    /// <param name="destination">destination を指定します。</param>
    private static void CorrectInto(
        Complex[] warped,
        int length,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        Complex[] destination)
    {
        var n = length;
        if (destination.Length < n || warped.Length < n)
        {
            throw new ArgumentException("Destination is shorter than source.", nameof(destination));
        }

        var map = BuildSourceIndexMap(n, sampleRate, amount, wowPhase, flutterPhase);
        var sum = ArrayPool<double>.Shared.Rent(n);
        var count = ArrayPool<int>.Shared.Rent(n);
        try
        {
            Array.Clear(sum, 0, n);
            Array.Clear(count, 0, n);
            for (var i = 0; i < n; i++)
            {
                var src = map[i];
                sum[src] += warped[i].Real;
                count[src]++;
            }

            for (var i = 0; i < n; i++)
            {
                destination[i] = new Complex(sum[i] / count[i], 0.0);
            }
        }
        finally
        {
            ArrayPool<double>.Shared.Return(sum);
            ArrayPool<int>.Shared.Return(count);
        }
    }

    /// <summary>
    /// オーバーサンプリング線形補間で変調を付与します。
    /// </summary>
    /// <param name="source">入力波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <param name="oversample">オーバーサンプル倍率。</param>
    /// <returns>変調後波形。</returns>
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
    /// オーバーサンプリング線形補間で逆補正します。
    /// </summary>
    /// <param name="warped">変調済み波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <param name="oversample">オーバーサンプル倍率。</param>
    /// <returns>補正後波形。</returns>
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

    /// <summary>
    /// 複素波形へオーバーサンプリング線形補間で変調を付与します。
    /// </summary>
    /// <param name="source">入力複素波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <param name="oversample">オーバーサンプル倍率。</param>
    /// <returns>変調後複素波形。</returns>
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

    /// <summary>
    /// 複素波形をオーバーサンプリング線形補間で逆補正します。
    /// </summary>
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量。</param>
    /// <param name="wowPhase">wow 位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 位相（ラジアン）。</param>
    /// <param name="oversample">オーバーサンプル倍率。</param>
    /// <returns>補正後複素波形。</returns>
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

    /// <summary>
    /// 速度プロファイルから累積時間軸と正規化係数を算出します。
    /// </summary>
    /// <param name="speedProfile">speedProfile を指定します。</param>
    /// <param name="cumul">cumul を指定します。</param>
    /// <param name="scale">scale を指定します。</param>
    private static void BuildCumul(ReadOnlySpan<double> speedProfile, out double[] cumul, out double scale)
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

    /// <summary>
    /// 配列版の累積時間軸算出ヘルパーです。
    /// </summary>
    /// <param name="speedProfile">speedProfile を指定します。</param>
    /// <param name="cumul">cumul を指定します。</param>
    /// <param name="scale">scale を指定します。</param>
    private static void BuildCumul(double[] speedProfile, out double[] cumul, out double scale) =>
        BuildCumul((ReadOnlySpan<double>)speedProfile, out cumul, out scale);

    /// <summary>
    /// 線形補間で波形をオーバーサンプリングします。
    /// </summary>
    /// <param name="source">source を指定します。</param>
    /// <param name="factor">factor を指定します。</param>
    /// <returns>処理結果。</returns>
    private static double[] UpsampleLinear(ReadOnlySpan<double> source, int factor)
    {
        if (source.Length == 0)
        {
            return [];
        }

        factor = Math.Max(1, factor);
        if (factor == 1)
        {
            return source.ToArray();
        }

        var nUp = ((source.Length - 1) * factor) + 1;
        var up = new double[nUp];

        var width = Vector<double>.Count;
        Span<double> lane = stackalloc double[width];
        for (var i = 0; i < width; i++)
        {
            lane[i] = i;
        }

        var laneVec = new Vector<double>(lane);
        var invFactor = 1.0 / factor;
        var invFactorVec = new Vector<double>(invFactor);
        var dstOffset = 0;

        for (var segment = 0; segment < source.Length - 1; segment++)
        {
            var a = source[segment];
            var delta = source[segment + 1] - a;
            var aVec = new Vector<double>(a);
            var deltaVec = new Vector<double>(delta);

            var t = 0;
            for (; t <= factor - width; t += width)
            {
                var tVec = laneVec + new Vector<double>((double)t);
                var fracVec = tVec * invFactorVec;
                var values = aVec + (deltaVec * fracVec);
                values.CopyTo(up.AsSpan(dstOffset + t, width));
            }

            for (; t < factor; t++)
            {
                up[dstOffset + t] = a + (delta * (t * invFactor));
            }

            dstOffset += factor;
        }

        up[^1] = source[^1];

        return up;
    }

    /// <summary>
    /// 指定位置の値を線形補間でサンプリングします。
    /// </summary>
    /// <param name="samples">samples を指定します。</param>
    /// <param name="position">position を指定します。</param>
    /// <returns>処理結果。</returns>
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


