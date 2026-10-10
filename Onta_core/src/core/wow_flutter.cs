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
    /// <summary>
    /// wow 成分の変調周波数（Hz）です。
    /// </summary>

    public const double WowFrequencyHz = 0.5;
    /// <summary>
    /// flutter 成分の変調周波数（Hz）です。
    /// </summary>

    public const double FlutterFrequencyHz = 6.0;
    /// <summary>
    /// オーバーサンプリング補間の既定倍率です。
    /// </summary>

    public const int DefaultOversample = 8;

    /// <summary>
    /// 受信側がワウ・フラッターとして探索・補正する変調量（ピーク相対速度偏差）の上限です（0.5%）。
    /// </summary>
    public const double MaxCorrectableAmount = 0.005;

    /// <summary>
    /// シード値から wow/flutter の初期位相を生成します。
    /// </summary>
    /// <param name="seed">乱数シード。0 の場合は非固定シード。</param>
    /// <returns>wow 位相と flutter 位相（ラジアン）。</returns>
    public static (double WowPhase, double FlutterPhase) CreatePhases(int seed)
    {
        var effectiveSeed = seed == 0 ? Random.Shared.Next() : seed;
        var state = MSequence31.InitializeState(MSequenceUsage.WowFlutterPhase, effectiveSeed);
        var wowPhase = MSequence31.NextUnitDouble(ref state) * 2.0 * Math.PI;
        var flutterPhase = MSequence31.NextUnitDouble(ref state) * 2.0 * Math.PI;
        return (wowPhase, flutterPhase);
    }

    /// <summary>
    /// 速度変調プロファイルを生成します。
    /// </summary>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（0 で無変調）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// 指定サンプル位置の相対速度（1.0 = 無変調）を返します。
    /// </summary>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ／フラッター変調量（相対速度振幅。0 で無変調）。</param>
    /// <param name="wowPhase">wow 成分の初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 成分の初期位相（ラジアン）。</param>
    /// <param name="sampleIndex">評価するサンプル位置（0 始まり）。</param>
    /// <returns>相対速度（1.0 = 無変調、下限 0.05）。</returns>
    public static double EvaluateSpeed(
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        long sampleIndex)
    {
        if (sampleIndex < 0)
        {
            sampleIndex = 0;
        }

        var sr = Math.Max(1, sampleRate);
        var wowAngle = wowPhase + (sampleIndex * (2.0 * Math.PI * WowFrequencyHz / sr));
        var flutterAngle = flutterPhase + (sampleIndex * (2.0 * Math.PI * FlutterFrequencyHz / sr));
        var modulation = (0.65 * Math.Sin(wowAngle)) + (0.35 * Math.Sin(flutterAngle));
        var speed = 1.0 + (amount * modulation);
        return speed < 0.05 ? 0.05 : speed;
    }

    /// <summary>
    /// 指定サンプル位置の速度偏差を百分率で返します（(speed-1)×100）。
    /// </summary>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ／フラッター変調量（相対速度振幅。0 で無変調）。</param>
    /// <param name="wowPhase">wow 成分の初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 成分の初期位相（ラジアン）。</param>
    /// <param name="sampleIndex">評価するサンプル位置（0 始まり）。</param>
    /// <returns>速度偏差（%）。正が速め、負が遅め。</returns>
    public static double EvaluateSpeedDeviationPercent(
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase,
        long sampleIndex) =>
        (EvaluateSpeed(sampleRate, amount, wowPhase, flutterPhase, sampleIndex) - 1.0) * 100.0;

    /// <summary>
    /// 速度変調プロファイルを既存バッファへ書き込みます。
    /// </summary>
    /// <param name="profile">書き込み先の相対速度バッファ（長さ分すべて上書き）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ／フラッター変調量（相対速度振幅。0 で無変調）。</param>
    /// <param name="wowPhase">wow 成分の初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 成分の初期位相（ラジアン）。</param>
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
    /// <param name="amount">変調量（0 で恒等マップ）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
        var cumulModel = new AnalyticCumulative(wowGain, wowPhase, wowOmega, flutterGain, flutterPhase, flutterOmega);

        var count = ArrayPool<int>.Shared.Rent(sampleCount);
        var cumul = ArrayPool<double>.Shared.Rent(sampleCount);
        try
        {
            var last = sampleCount - 1;
            var cumulEnd = cumulModel.At(last);
            var scale = cumulEnd > 1e-12 ? last / cumulEnd : 1.0;
            var map = new int[sampleCount];
            Array.Clear(count, 0, sampleCount);

            // SumOfSines と同じ解析累積。速度漸化で進め、ScatterReanchorEvery サンプルごとに解析値へ再同期する。
            // 再同期点から先は前の区間に依存しないので、区間ごとに独立して（SIMD では 4 区間並列で）計算できる。
            var walk = new SourceIndexWalk(
                cumulModel,
                scale,
                wowPhase,
                flutterPhase,
                wowOmega,
                flutterOmega,
                wowGain,
                flutterGain,
                cumul.AsSpan(0, sampleCount),
                map,
                count.AsSpan(0, sampleCount));
            var blockStart = 0;
            if (Avx.IsSupported)
            {
                for (; blockStart + (4 * ScatterReanchorEvery) <= sampleCount; blockStart += 4 * ScatterReanchorEvery)
                {
                    walk.RunBlocksAvx(blockStart);
                }
            }

            for (; blockStart < sampleCount; blockStart += ScatterReanchorEvery)
            {
                walk.RunBlockScalar(blockStart, Math.Min(blockStart + ScatterReanchorEvery, sampleCount));
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
    /// <param name="amount">変調量（0 で無変調）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="amount">変調量（0 で無変調）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="referenceLength">scale 算出に使う全波形相当の長さ（サンプル数）。</param>
    /// <param name="prefixStart">補正対象プレフィックスの開始インデックス。</param>
    /// <param name="prefixLength">補正対象プレフィックスの長さ（サンプル数）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="destination">補正結果の書き込み先（長さは prefixLength 以上）。</param>
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
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="referenceLength">scale 算出に使う全波形相当の長さ（サンプル数）。</param>
    /// <param name="prefixStart">補正対象プレフィックスの開始インデックス。</param>
    /// <param name="prefixLength">補正対象プレフィックスの長さ（サンプル数）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="idealReals">理想波形の実部（長さは prefixLength 以上）。</param>
    /// <param name="meanIdeal">idealReals の平均。</param>
    /// <param name="energyIdeal">idealReals の平均除去後エネルギー（分散×長さ）。</param>
    /// <param name="sumScratch">散乱補正の合計ワーク領域（長さは prefixLength 以上）。</param>
    /// <param name="countScratch">散乱補正の件数ワーク領域（長さは prefixLength 以上）。</param>
    /// <returns>正規化相関係数。入力不足時は <see cref="double.NegativeInfinity"/>。</returns>
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

        if (Avx.IsSupported && prefixLength >= 8 && energyIdeal > 1e-18)
        {
            return AverageAndCorrelateAvx(
                warped,
                prefixStart,
                idealReals[..prefixLength],
                meanIdeal,
                energyIdeal,
                sumScratch[..prefixLength],
                countScratch[..prefixLength]);
        }

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
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="referenceLength">scale 算出に使う全波形相当の長さ（サンプル数）。</param>
    /// <param name="prefixStart">補正対象プレフィックスの開始インデックス。</param>
    /// <param name="prefixLength">補正対象プレフィックスの長さ（サンプル数）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">ワウ／フラッター変調量（相対速度振幅）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="sum">プレフィックス各点への寄与合計（実部）。</param>
    /// <param name="count">プレフィックス各点への寄与件数。</param>
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
        var cumulModel = new AnalyticCumulative(wowGain, wowPhase, wowOmega, flutterGain, flutterPhase, flutterOmega);

        var absEnd = referenceLength - 1;
        var cumulEnd = cumulModel.At(absEnd);
        var scale = cumulEnd > 1e-12 ? absEnd / cumulEnd : 1.0;
        var prefixEnd = prefixStart + prefixLength;

        // amount≈0.01 では map[i]≈i。プレフィックスへ寄与する i は近傍に閉じる。
        var slack = Math.Max(sampleRate / 5, (int)(prefixLength * 0.05) + 64);
        var iMin = Math.Max(0, prefixStart - slack);
        var iMax = Math.Min(warped.Length, prefixEnd + slack);

        sum.Clear();
        count.Clear();

        // SumOfSines 毎サンプルより speed 漸化が安い。さらに Math.Sin 連打を避け、
        // 複素回転で sin を進め、ScatterReanchorEvery サンプルごとに解析角へ再同期してドリフトを抑える。
        // 再同期点から先は前の区間に依存しないので、区間ごとに独立して（SIMD では 4 区間並列で）計算できる。
        var walk = new ScatterWalk(
            warped,
            prefixStart,
            prefixLength,
            scale,
            wowPhase,
            flutterPhase,
            wowOmega,
            flutterOmega,
            wowGain,
            flutterGain);
        Span<int> starts = stackalloc int[4];
        Span<double> startCumuls = stackalloc double[4];
        var pending = 0;
        var cumulBlockStart = cumulModel.At(iMin);
        for (var blockStart = iMin; blockStart < iMax; blockStart += ScatterReanchorEvery)
        {
            var blockEnd = Math.Min(blockStart + ScatterReanchorEvery, iMax);

            // 写像先は i について単調増加。区間がまるごとプレフィックスの先／手前なら打ち切る／飛ばす（再同期のずれ分の余裕を持たせる）
            var cumulStart = cumulBlockStart;
            if ((cumulStart * scale) + 0.5 >= prefixEnd + ScatterSkipMargin)
            {
                break;
            }

            cumulBlockStart = cumulModel.At(blockEnd);
            if ((cumulBlockStart * scale) + 0.5 < prefixStart - ScatterSkipMargin)
            {
                continue;
            }

            if (Avx.IsSupported && blockEnd - blockStart == ScatterReanchorEvery)
            {
                starts[pending] = blockStart;
                startCumuls[pending] = cumulStart;
                pending++;
                if (pending == starts.Length)
                {
                    walk.RunBlocksAvx(starts, startCumuls, sum, count);
                    pending = 0;
                }

                continue;
            }

            walk.RunBlockScalar(blockStart, blockEnd, cumulStart, sum, count);
        }

        for (var p = 0; p < pending; p++)
        {
            walk.RunBlockScalar(starts[p], starts[p] + ScatterReanchorEvery, startCumuls[p], sum, count);
        }
    }

    /// <summary>
    /// 解析的 SumOfSines モデルでの累積時間（サンプル i までの速度の和）を求めます。
    /// 区間によらず一定の sin(ω/2) を先に求めておきます。
    /// </summary>
    private readonly struct AnalyticCumulative
    {
        private readonly double _wowGain;
        private readonly double _wowPhase;
        private readonly double _wowHalf;
        private readonly double _wowDenom;
        private readonly double _flutterGain;
        private readonly double _flutterPhase;
        private readonly double _flutterHalf;
        private readonly double _flutterDenom;

        /// <summary>
        /// モデルのパラメータを保持します。
        /// </summary>
        /// <param name="wowGain">wow 成分の速度振幅。</param>
        /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
        /// <param name="wowOmega">wow の 1 サンプルあたりの角度。</param>
        /// <param name="flutterGain">flutter 成分の速度振幅。</param>
        /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
        /// <param name="flutterOmega">flutter の 1 サンプルあたりの角度。</param>
        public AnalyticCumulative(
            double wowGain,
            double wowPhase,
            double wowOmega,
            double flutterGain,
            double flutterPhase,
            double flutterOmega)
        {
            _wowGain = wowGain;
            _wowPhase = wowPhase;
            _wowHalf = wowOmega * 0.5;
            _wowDenom = Math.Sin(_wowHalf);
            _flutterGain = flutterGain;
            _flutterPhase = flutterPhase;
            _flutterHalf = flutterOmega * 0.5;
            _flutterDenom = Math.Sin(_flutterHalf);
        }

        /// <summary>
        /// サンプル i での累積時間を返します（<see cref="SumOfSines"/> を使った式と同じ値）。
        /// </summary>
        /// <param name="i">サンプルインデックス。</param>
        /// <returns>累積サンプル相当値（i ≤ 0 なら 0）。</returns>
        public double At(int i)
        {
            if (i <= 0)
            {
                return 0.0;
            }

            return i
                + (_wowGain * SumOfSines(_wowPhase, _wowHalf, _wowDenom, i))
                + (_flutterGain * SumOfSines(_flutterPhase, _flutterHalf, _flutterDenom, i));
        }

        /// <summary>
        /// sin(phase0 + k·ω) を k=0..count-1 で解析的に合計します（count ≥ 1）。
        /// </summary>
        /// <param name="phase0">初項の位相（ラジアン）。</param>
        /// <param name="half">ω/2。</param>
        /// <param name="denom">sin(ω/2)。</param>
        /// <param name="count">合計する項数。</param>
        /// <returns>正弦和。</returns>
        private static double SumOfSines(double phase0, double half, double denom, int count)
        {
            if (Math.Abs(denom) < 1e-12)
            {
                return count * Math.Sin(phase0);
            }

            var numer = Math.Sin(count * half);
            return numer / denom * Math.Sin(phase0 + ((count - 1) * half));
        }
    }

    /// <summary>
    /// <see cref="BuildSourceIndexMap"/> の 1 区間（再同期点から次の再同期点まで）を進める計算です。
    /// </summary>
    private readonly ref struct SourceIndexWalk
    {
        private readonly AnalyticCumulative _cumulModel;
        private readonly double _scale;
        private readonly double _wowPhase;
        private readonly double _flutterPhase;
        private readonly double _wowOmega;
        private readonly double _flutterOmega;
        private readonly double _wowGain;
        private readonly double _flutterGain;
        private readonly double _cosWowStep;
        private readonly double _sinWowStep;
        private readonly double _cosFlutterStep;
        private readonly double _sinFlutterStep;
        private readonly Span<double> _cumul;
        private readonly Span<int> _map;
        private readonly Span<int> _count;

        /// <summary>
        /// 共通パラメータと出力先を保持します。
        /// </summary>
        /// <param name="cumulModel">解析累積モデル。</param>
        /// <param name="scale">累積時間から入力インデックスへの倍率。</param>
        /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
        /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
        /// <param name="wowOmega">wow の 1 サンプルあたりの角度。</param>
        /// <param name="flutterOmega">flutter の 1 サンプルあたりの角度。</param>
        /// <param name="wowGain">wow 成分の速度振幅。</param>
        /// <param name="flutterGain">flutter 成分の速度振幅。</param>
        /// <param name="cumul">各サンプルの累積時間の出力先（長さ = サンプル数）。</param>
        /// <param name="map">入力インデックス表の出力先（長さ = サンプル数）。</param>
        /// <param name="count">参照回数（長さ = サンプル数、0 クリア済み）。</param>
        public SourceIndexWalk(
            AnalyticCumulative cumulModel,
            double scale,
            double wowPhase,
            double flutterPhase,
            double wowOmega,
            double flutterOmega,
            double wowGain,
            double flutterGain,
            Span<double> cumul,
            Span<int> map,
            Span<int> count)
        {
            if (map.Length != cumul.Length || count.Length != cumul.Length)
            {
                throw new ArgumentException("Output buffers must have the same length.");
            }

            _cumulModel = cumulModel;
            _scale = scale;
            _wowPhase = wowPhase;
            _flutterPhase = flutterPhase;
            _wowOmega = wowOmega;
            _flutterOmega = flutterOmega;
            _wowGain = wowGain;
            _flutterGain = flutterGain;
            _cosWowStep = Math.Cos(wowOmega);
            _sinWowStep = Math.Sin(wowOmega);
            _cosFlutterStep = Math.Cos(flutterOmega);
            _sinFlutterStep = Math.Sin(flutterOmega);
            _cumul = cumul;
            _map = map;
            _count = count;
        }

        /// <summary>
        /// 区間先頭での wow/flutter の sin・cos を求めます。
        /// </summary>
        /// <param name="start">区間先頭のサンプルインデックス。</param>
        /// <param name="wowSin">wow の sin。</param>
        /// <param name="wowCos">wow の cos。</param>
        /// <param name="flutterSin">flutter の sin。</param>
        /// <param name="flutterCos">flutter の cos。</param>
        private void AnglesAt(int start, out double wowSin, out double wowCos, out double flutterSin, out double flutterCos)
        {
            var wowAngle = start == 0 ? _wowPhase : _wowPhase + (start * _wowOmega);
            var flutterAngle = start == 0 ? _flutterPhase : _flutterPhase + (start * _flutterOmega);
            wowSin = Math.Sin(wowAngle);
            wowCos = Math.Cos(wowAngle);
            flutterSin = Math.Sin(flutterAngle);
            flutterCos = Math.Cos(flutterAngle);
        }

        /// <summary>
        /// 写像先を求めて範囲内へ丸めます。
        /// </summary>
        /// <param name="c">累積時間。</param>
        /// <returns>入力インデックス（0〜サンプル数−1）。</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int IndexOf(double c)
        {
            var last = _cumul.Length - 1;
            var idx = RoundAwayFromZeroPositive(c * _scale);
            if ((uint)idx > (uint)last)
            {
                idx = idx < 0 ? 0 : last;
            }

            return idx;
        }

        /// <summary>
        /// 区間 [start, end) をスカラで進め、累積時間・写像先・参照回数を書き込みます。
        /// </summary>
        /// <param name="start">区間先頭（再同期点）。</param>
        /// <param name="end">区間末尾（含まない）。</param>
        public void RunBlockScalar(int start, int end)
        {
            var c = _cumulModel.At(start);
            AnglesAt(start, out var wowSin, out var wowCos, out var flutterSin, out var flutterCos);
            for (var i = start; i < end; i++)
            {
                _cumul[i] = c;
                var idx = IndexOf(c);
                _map[i] = idx;
                _count[idx]++;

                c += 1.0 + (_wowGain * wowSin) + (_flutterGain * flutterSin);

                var nextWowSin = (wowSin * _cosWowStep) + (wowCos * _sinWowStep);
                var nextWowCos = (wowCos * _cosWowStep) - (wowSin * _sinWowStep);
                wowSin = nextWowSin;
                wowCos = nextWowCos;

                var nextFlutterSin = (flutterSin * _cosFlutterStep) + (flutterCos * _sinFlutterStep);
                var nextFlutterCos = (flutterCos * _cosFlutterStep) - (flutterSin * _sinFlutterStep);
                flutterSin = nextFlutterSin;
                flutterCos = nextFlutterCos;
            }
        }

        /// <summary>
        /// first から連続する長さ ScatterReanchorEvery の 4 区間を AVX の 4 レーンで並列に進めます（各レーンの演算はスカラ版と同じ順序）。
        /// </summary>
        /// <param name="first">先頭区間の開始（再同期点）。4 区間とも出力範囲内であること。</param>
        public void RunBlocksAvx(int first)
        {
            const int Block = ScatterReanchorEvery;
            if (first < 0 || first > _cumul.Length - (4 * Block))
            {
                throw new ArgumentOutOfRangeException(nameof(first));
            }

            Span<double> init = stackalloc double[4 * 5];
            for (var lane = 0; lane < 4; lane++)
            {
                var start = first + (lane * Block);
                init[lane] = _cumulModel.At(start);
                AnglesAt(start, out init[4 + lane], out init[8 + lane], out init[12 + lane], out init[16 + lane]);
            }

            var c = Vector256.Create<double>(init[..4]);
            var wowSin = Vector256.Create<double>(init.Slice(4, 4));
            var wowCos = Vector256.Create<double>(init.Slice(8, 4));
            var flutterSin = Vector256.Create<double>(init.Slice(12, 4));
            var flutterCos = Vector256.Create<double>(init.Slice(16, 4));
            var scale = Vector256.Create(_scale);
            var half = Vector256.Create(0.5);
            var one = Vector256.Create(1.0);
            var wowGain = Vector256.Create(_wowGain);
            var flutterGain = Vector256.Create(_flutterGain);
            var cosWowStep = Vector256.Create(_cosWowStep);
            var sinWowStep = Vector256.Create(_sinWowStep);
            var cosFlutterStep = Vector256.Create(_cosFlutterStep);
            var sinFlutterStep = Vector256.Create(_sinFlutterStep);
            var zero = Vector128<int>.Zero;
            var lastIndex = Vector128.Create(_cumul.Length - 1);

            ref var cumul0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(_cumul), first);
            ref var map0 = ref Unsafe.Add(ref MemoryMarshal.GetReference(_map), first);
            ref var countRef = ref MemoryMarshal.GetReference(_count);
            for (var j = 0; j < Block; j++)
            {
                var idx = Avx.ConvertToVector128Int32WithTruncation(Avx.Add(Avx.Multiply(c, scale), half));
                idx = Sse41.Min(Sse41.Max(idx, zero), lastIndex);
                var cLow = c.GetLower();
                var cHigh = c.GetUpper();
                Unsafe.Add(ref cumul0, j) = cLow.ToScalar();
                Unsafe.Add(ref cumul0, Block + j) = cLow.GetElement(1);
                Unsafe.Add(ref cumul0, (2 * Block) + j) = cHigh.ToScalar();
                Unsafe.Add(ref cumul0, (3 * Block) + j) = cHigh.GetElement(1);
                var i0 = idx.ToScalar();
                var i1 = idx.GetElement(1);
                var i2 = idx.GetElement(2);
                var i3 = idx.GetElement(3);
                Unsafe.Add(ref map0, j) = i0;
                Unsafe.Add(ref map0, Block + j) = i1;
                Unsafe.Add(ref map0, (2 * Block) + j) = i2;
                Unsafe.Add(ref map0, (3 * Block) + j) = i3;
                Unsafe.Add(ref countRef, i0)++;
                Unsafe.Add(ref countRef, i1)++;
                Unsafe.Add(ref countRef, i2)++;
                Unsafe.Add(ref countRef, i3)++;

                c = Avx.Add(
                    c,
                    Avx.Add(Avx.Add(one, Avx.Multiply(wowGain, wowSin)), Avx.Multiply(flutterGain, flutterSin)));

                var nextWowSin = Avx.Add(Avx.Multiply(wowSin, cosWowStep), Avx.Multiply(wowCos, sinWowStep));
                var nextWowCos = Avx.Subtract(Avx.Multiply(wowCos, cosWowStep), Avx.Multiply(wowSin, sinWowStep));
                wowSin = nextWowSin;
                wowCos = nextWowCos;

                var nextFlutterSin = Avx.Add(Avx.Multiply(flutterSin, cosFlutterStep), Avx.Multiply(flutterCos, sinFlutterStep));
                var nextFlutterCos = Avx.Subtract(Avx.Multiply(flutterCos, cosFlutterStep), Avx.Multiply(flutterSin, sinFlutterStep));
                flutterSin = nextFlutterSin;
                flutterCos = nextFlutterCos;
            }
        }
    }

    /// <summary>散乱 Correct で sin を解析角へ再同期する間隔（サンプル）。</summary>
    private const int ScatterReanchorEvery = 512;

    /// <summary>区間を飛ばす判定の余裕（サンプル）。漸化と解析値のずれより十分大きくする。</summary>
    private const double ScatterSkipMargin = 2.0;

    /// <summary>
    /// 散乱 Correct の 1 区間（再同期点から次の再同期点まで）を進める計算です。
    /// </summary>
    private readonly ref struct ScatterWalk
    {
        private readonly Complex[] _warped;
        private readonly int _prefixStart;
        private readonly int _prefixLength;
        private readonly double _scale;
        private readonly double _wowPhase;
        private readonly double _flutterPhase;
        private readonly double _wowOmega;
        private readonly double _flutterOmega;
        private readonly double _wowGain;
        private readonly double _flutterGain;
        private readonly double _cosWowStep;
        private readonly double _sinWowStep;
        private readonly double _cosFlutterStep;
        private readonly double _sinFlutterStep;

        /// <summary>
        /// 散乱 Correct の共通パラメータを保持します。
        /// </summary>
        /// <param name="warped">変調済み複素波形。</param>
        /// <param name="prefixStart">補正対象プレフィックスの開始インデックス。</param>
        /// <param name="prefixLength">補正対象プレフィックスの長さ。</param>
        /// <param name="scale">累積時間から入力インデックスへの倍率。</param>
        /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
        /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
        /// <param name="wowOmega">wow の 1 サンプルあたりの角度。</param>
        /// <param name="flutterOmega">flutter の 1 サンプルあたりの角度。</param>
        /// <param name="wowGain">wow 成分の速度振幅。</param>
        /// <param name="flutterGain">flutter 成分の速度振幅。</param>
        public ScatterWalk(
            Complex[] warped,
            int prefixStart,
            int prefixLength,
            double scale,
            double wowPhase,
            double flutterPhase,
            double wowOmega,
            double flutterOmega,
            double wowGain,
            double flutterGain)
        {
            _warped = warped;
            _prefixStart = prefixStart;
            _prefixLength = prefixLength;
            _scale = scale;
            _wowPhase = wowPhase;
            _flutterPhase = flutterPhase;
            _wowOmega = wowOmega;
            _flutterOmega = flutterOmega;
            _wowGain = wowGain;
            _flutterGain = flutterGain;
            _cosWowStep = Math.Cos(wowOmega);
            _sinWowStep = Math.Sin(wowOmega);
            _cosFlutterStep = Math.Cos(flutterOmega);
            _sinFlutterStep = Math.Sin(flutterOmega);
        }

        /// <summary>
        /// 再同期点 start から end まで 1 区間をスカラで進め、写像先がプレフィックス内なら sum/count へ加えます。
        /// </summary>
        /// <param name="start">区間先頭（再同期点）。</param>
        /// <param name="end">区間末尾（含まない。start から ScatterReanchorEvery 以内）。</param>
        /// <param name="cumulStart">start での累積時間（解析値）。</param>
        /// <param name="sum">プレフィックス各点への寄与合計。</param>
        /// <param name="count">プレフィックス各点への寄与件数。</param>
        public void RunBlockScalar(int start, int end, double cumulStart, Span<double> sum, Span<int> count)
        {
            var cumul = cumulStart;
            var wowAngle = _wowPhase + (start * _wowOmega);
            var flutterAngle = _flutterPhase + (start * _flutterOmega);
            var wowSin = Math.Sin(wowAngle);
            var wowCos = Math.Cos(wowAngle);
            var flutterSin = Math.Sin(flutterAngle);
            var flutterCos = Math.Cos(flutterAngle);
            for (var i = start; i < end; i++)
            {
                var src = RoundAwayFromZeroPositive(cumul * _scale);
                if ((uint)(src - _prefixStart) < (uint)_prefixLength)
                {
                    var local = src - _prefixStart;
                    sum[local] += _warped[i].Real;
                    count[local]++;
                }

                cumul += 1.0 + (_wowGain * wowSin) + (_flutterGain * flutterSin);

                var nextWowSin = (wowSin * _cosWowStep) + (wowCos * _sinWowStep);
                var nextWowCos = (wowCos * _cosWowStep) - (wowSin * _sinWowStep);
                wowSin = nextWowSin;
                wowCos = nextWowCos;

                var nextFlutterSin = (flutterSin * _cosFlutterStep) + (flutterCos * _sinFlutterStep);
                var nextFlutterCos = (flutterCos * _cosFlutterStep) - (flutterSin * _sinFlutterStep);
                flutterSin = nextFlutterSin;
                flutterCos = nextFlutterCos;
            }
        }

        /// <summary>
        /// 長さ ScatterReanchorEvery の 4 区間を AVX の 4 レーンで並列に進めます（各レーンの演算はスカラ版と同じ順序）。
        /// </summary>
        /// <param name="starts">4 区間の先頭（再同期点）。</param>
        /// <param name="startCumuls">4 区間の先頭での累積時間（解析値）。</param>
        /// <param name="sum">プレフィックス各点への寄与合計。</param>
        /// <param name="count">プレフィックス各点への寄与件数。</param>
        public void RunBlocksAvx(ReadOnlySpan<int> starts, ReadOnlySpan<double> startCumuls, Span<double> sum, Span<int> count)
        {
            Span<double> init = stackalloc double[4 * 5];
            for (var lane = 0; lane < 4; lane++)
            {
                var start = starts[lane];
                var wowAngle = _wowPhase + (start * _wowOmega);
                var flutterAngle = _flutterPhase + (start * _flutterOmega);
                init[lane] = startCumuls[lane];
                init[4 + lane] = Math.Sin(wowAngle);
                init[8 + lane] = Math.Cos(wowAngle);
                init[12 + lane] = Math.Sin(flutterAngle);
                init[16 + lane] = Math.Cos(flutterAngle);
            }

            var cumul = Vector256.Create<double>(init[..4]);
            var wowSin = Vector256.Create<double>(init.Slice(4, 4));
            var wowCos = Vector256.Create<double>(init.Slice(8, 4));
            var flutterSin = Vector256.Create<double>(init.Slice(12, 4));
            var flutterCos = Vector256.Create<double>(init.Slice(16, 4));
            var scale = Vector256.Create(_scale);
            var half = Vector256.Create(0.5);
            var one = Vector256.Create(1.0);
            var wowGain = Vector256.Create(_wowGain);
            var flutterGain = Vector256.Create(_flutterGain);
            var cosWowStep = Vector256.Create(_cosWowStep);
            var sinWowStep = Vector256.Create(_sinWowStep);
            var cosFlutterStep = Vector256.Create(_cosFlutterStep);
            var sinFlutterStep = Vector256.Create(_sinFlutterStep);
            if (sum.Length < _prefixLength || count.Length < _prefixLength)
            {
                throw new ArgumentException("Scratch buffers are shorter than prefix length.");
            }

            for (var lane = 0; lane < 4; lane++)
            {
                if (starts[lane] < 0 || starts[lane] > _warped.Length - ScatterReanchorEvery)
                {
                    throw new ArgumentOutOfRangeException(nameof(starts));
                }
            }

            ref var sumRef = ref MemoryMarshal.GetReference(sum);
            ref var countRef = ref MemoryMarshal.GetReference(count);
            ref var warpedRef = ref MemoryMarshal.GetArrayDataReference(_warped);
            ref var w0 = ref Unsafe.Add(ref warpedRef, starts[0]);
            ref var w1 = ref Unsafe.Add(ref warpedRef, starts[1]);
            ref var w2 = ref Unsafe.Add(ref warpedRef, starts[2]);
            ref var w3 = ref Unsafe.Add(ref warpedRef, starts[3]);
            for (var j = 0; j < ScatterReanchorEvery; j++)
            {
                var src = Avx.ConvertToVector128Int32WithTruncation(Avx.Add(Avx.Multiply(cumul, scale), half));
                Accumulate(src.ToScalar(), Unsafe.Add(ref w0, j).Real, ref sumRef, ref countRef);
                Accumulate(src.GetElement(1), Unsafe.Add(ref w1, j).Real, ref sumRef, ref countRef);
                Accumulate(src.GetElement(2), Unsafe.Add(ref w2, j).Real, ref sumRef, ref countRef);
                Accumulate(src.GetElement(3), Unsafe.Add(ref w3, j).Real, ref sumRef, ref countRef);

                cumul = Avx.Add(
                    cumul,
                    Avx.Add(Avx.Add(one, Avx.Multiply(wowGain, wowSin)), Avx.Multiply(flutterGain, flutterSin)));

                var nextWowSin = Avx.Add(Avx.Multiply(wowSin, cosWowStep), Avx.Multiply(wowCos, sinWowStep));
                var nextWowCos = Avx.Subtract(Avx.Multiply(wowCos, cosWowStep), Avx.Multiply(wowSin, sinWowStep));
                wowSin = nextWowSin;
                wowCos = nextWowCos;

                var nextFlutterSin = Avx.Add(Avx.Multiply(flutterSin, cosFlutterStep), Avx.Multiply(flutterCos, sinFlutterStep));
                var nextFlutterCos = Avx.Subtract(Avx.Multiply(flutterCos, cosFlutterStep), Avx.Multiply(flutterSin, sinFlutterStep));
                flutterSin = nextFlutterSin;
                flutterCos = nextFlutterCos;
            }
        }

        /// <summary>
        /// 写像先 src がプレフィックス内なら、入力の実部を加えます。
        /// </summary>
        /// <param name="src">写像先の入力インデックス。</param>
        /// <param name="value">寄与する入力の実部。</param>
        /// <param name="sumRef">プレフィックス各点への寄与合計（長さ ≥ プレフィックス長）の先頭。</param>
        /// <param name="countRef">プレフィックス各点への寄与件数（長さ ≥ プレフィックス長）の先頭。</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Accumulate(int src, double value, ref double sumRef, ref int countRef)
        {
            var local = src - _prefixStart;
            if ((uint)local < (uint)_prefixLength)
            {
                Unsafe.Add(ref sumRef, local) += value;
                Unsafe.Add(ref countRef, local)++;
            }
        }
    }

    /// <summary>
    /// 連続 double 系列の正規化相関係数（ideal 側 mean/energy 既知）。
    /// </summary>
    /// <param name="a">観測側の実数系列。</param>
    /// <param name="b">理想側の実数系列（長さは a 以上を想定）。</param>
    /// <param name="meanB">b の平均。</param>
    /// <param name="energyB">b の平均除去後エネルギー。</param>
    /// <returns>正規化相関係数。系列が短すぎる場合は <see cref="double.NegativeInfinity"/>。</returns>
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

    /// <summary>
    /// AVX 用に double 4 要素を非アライメントロードします。
    /// </summary>
    /// <param name="source">読み出し元の実数系列。</param>
    /// <param name="index">先頭要素のインデックス。</param>
    /// <returns>4 要素の AVX ベクトル。</returns>
    private static Vector256<double> LoadAvx(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector256<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// NEON 用に double 2 要素を非アライメントロードします。
    /// </summary>
    /// <param name="source">読み出し元の実数系列。</param>
    /// <param name="index">先頭要素のインデックス。</param>
    /// <returns>2 要素の NEON ベクトル。</returns>
    private static Vector128<double> LoadNeon(ReadOnlySpan<double> source, int index)
    {
        ref var first = ref MemoryMarshal.GetReference(source);
        ref var at = ref Unsafe.Add(ref first, index);
        return Unsafe.ReadUnaligned<Vector128<double>>(ref Unsafe.As<double, byte>(ref at));
    }

    /// <summary>
    /// <see cref="CorrelateDenseReals"/> の AVX 実装です。
    /// </summary>
    /// <param name="a">観測側の実数系列。</param>
    /// <param name="b">理想側の実数系列。</param>
    /// <param name="meanB">b の平均。</param>
    /// <param name="energyB">b の平均除去後エネルギー。</param>
    /// <param name="count">相関係数に使う有効サンプル数。</param>
    /// <returns>正規化相関係数。</returns>
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

        return CorrelateCenteredRealsAvx(a, b, sumA / count, meanB, energyB, count);
    }

    /// <summary>
    /// 散乱 Correct の sum/count を平均値（寄与なしの点は入力そのもの）へ置き換え、理想波形との正規化相関を返します。
    /// 結果は平均化ループの後に <see cref="CorrelateDenseReals"/> を呼ぶのと同じ値です。
    /// </summary>
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="prefixStart">プレフィックスの開始インデックス。</param>
    /// <param name="idealReals">理想波形（プレフィックス長）。</param>
    /// <param name="meanIdeal">理想波形の平均。</param>
    /// <param name="energyIdeal">理想波形の平均除去後エネルギー。</param>
    /// <param name="sum">寄与合計（プレフィックス長）。平均値で上書きします。</param>
    /// <param name="count">寄与件数（プレフィックス長）。</param>
    /// <returns>正規化相関係数。</returns>
    private static double AverageAndCorrelateAvx(
        Complex[] warped,
        int prefixStart,
        ReadOnlySpan<double> idealReals,
        double meanIdeal,
        double energyIdeal,
        Span<double> sum,
        ReadOnlySpan<int> count)
    {
        var length = sum.Length;
        ref var sumRef = ref MemoryMarshal.GetReference(sum);
        ref var countRef = ref MemoryMarshal.GetReference(count);
        var sumVec = Vector256<double>.Zero;
        var ones = Vector128.Create(1);
        var j = 0;
        for (; j + 4 <= length; j += 4)
        {
            var countVec = Vector128.LoadUnsafe(ref countRef, (nuint)j);
            var mean = Vector256.LoadUnsafe(ref sumRef, (nuint)j);

            // 寄与 1 件（大半の点）なら x / 1 = x なので除算を省く
            if (!Vector128.EqualsAll(countVec, ones))
            {
                var c = Avx.ConvertToVector256Double(countVec);
                mean = Avx.Divide(mean, c);
                mean.StoreUnsafe(ref sumRef, (nuint)j);
                var empty = Avx.MoveMask(Avx.CompareEqual(c, Vector256<double>.Zero));
                if (empty != 0)
                {
                    for (var lane = 0; lane < 4; lane++)
                    {
                        if ((empty & (1 << lane)) != 0)
                        {
                            sum[j + lane] = warped[Math.Clamp(prefixStart + j + lane, 0, warped.Length - 1)].Real;
                        }
                    }

                    mean = Vector256.LoadUnsafe(ref sumRef, (nuint)j);
                }
            }

            sumVec = Avx.Add(sumVec, mean);
        }

        var sumA = sumVec.GetElement(0) + sumVec.GetElement(1) + sumVec.GetElement(2) + sumVec.GetElement(3);
        for (; j < length; j++)
        {
            sum[j] = count[j] > 0
                ? sum[j] / count[j]
                : warped[Math.Clamp(prefixStart + j, 0, warped.Length - 1)].Real;
            sumA += sum[j];
        }

        return CorrelateCenteredRealsAvx(sum, idealReals, sumA / length, meanIdeal, energyIdeal, length);
    }

    /// <summary>
    /// 平均既知の 2 系列の正規化相関係数を AVX で求めます。
    /// </summary>
    /// <param name="a">観測側の実数系列。</param>
    /// <param name="b">理想側の実数系列。</param>
    /// <param name="meanA">a の平均。</param>
    /// <param name="meanB">b の平均。</param>
    /// <param name="energyB">b の平均除去後エネルギー。</param>
    /// <param name="count">相関係数に使う有効サンプル数。</param>
    /// <returns>正規化相関係数。</returns>
    private static double CorrelateCenteredRealsAvx(
        ReadOnlySpan<double> a,
        ReadOnlySpan<double> b,
        double meanA,
        double meanB,
        double energyB,
        int count)
    {
        var meanAVec = Vector256.Create(meanA);
        var meanBVec = Vector256.Create(meanB);
        var numVec = Vector256<double>.Zero;
        var energyAVec = Vector256<double>.Zero;
        var i = 0;
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

    /// <summary>
    /// <see cref="CorrelateDenseReals"/> の AdvSimd（ARM64）実装です。
    /// </summary>
    /// <param name="a">観測側の実数系列。</param>
    /// <param name="b">理想側の実数系列。</param>
    /// <param name="meanB">b の平均。</param>
    /// <param name="energyB">b の平均除去後エネルギー。</param>
    /// <param name="count">相関係数に使う有効サンプル数。</param>
    /// <returns>正規化相関係数。</returns>
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

    /// <summary>
    /// 非負値向けの AwayFromZero 丸め（Math.Round より軽量）。
    /// </summary>
    /// <param name="value">丸める非負の実数値。</param>
    /// <returns>最も近い整数（0.5 以上は切り上げ）。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundAwayFromZeroPositive(double value) =>
        (int)(value + 0.5);

    /// <summary>
    /// 複素波形の先頭指定長だけをインプレースで逆補正します。
    /// </summary>
    /// <param name="warped">補正対象の複素波形。</param>
    /// <param name="length">補正対象サンプル長。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="destination">補正結果の書き込み先（長さは warped 以上）。</param>
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
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="length">補正対象とする先頭サンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="destination">補正結果の書き込み先（長さは length 以上）。</param>
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

            var start = 0;
            if (Avx.IsSupported)
            {
                start = AverageToComplexAvx(sum.AsSpan(0, n), count.AsSpan(0, n), destination.AsSpan(0, n));
            }

            for (var i = start; i < n; i++)
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
    /// sum / count を実部、0 を虚部とする複素数を AVX で 4 点ずつ書き込みます（スカラの除算と同じ値）。
    /// </summary>
    /// <param name="sum">寄与合計。</param>
    /// <param name="count">寄与件数（sum と同じ長さ）。</param>
    /// <param name="destination">書き込み先（sum と同じ長さ）。</param>
    /// <returns>書き込んだ点数（4 の倍数）。残りは呼び出し側で処理する。</returns>
    private static int AverageToComplexAvx(ReadOnlySpan<double> sum, ReadOnlySpan<int> count, Span<Complex> destination)
    {
        var n = sum.Length;
        if (count.Length != n || destination.Length != n)
        {
            throw new ArgumentException("Buffers must have the same length.");
        }

        ref var sumRef = ref MemoryMarshal.GetReference(sum);
        ref var countRef = ref MemoryMarshal.GetReference(count);
        ref var destRef = ref Unsafe.As<Complex, double>(ref MemoryMarshal.GetReference(destination));
        var ones = Vector128.Create(1);
        var i = 0;
        for (; i + 4 <= n; i += 4)
        {
            var countVec = Vector128.LoadUnsafe(ref countRef, (nuint)i);
            var mean = Vector256.LoadUnsafe(ref sumRef, (nuint)i);

            // 寄与 1 件（大半の点）なら x / 1 = x なので除算を省く
            if (!Vector128.EqualsAll(countVec, ones))
            {
                mean = Avx.Divide(mean, Avx.ConvertToVector256Double(countVec));
            }

            var low = Avx.UnpackLow(mean, Vector256<double>.Zero);
            var high = Avx.UnpackHigh(mean, Vector256<double>.Zero);
            Avx.Permute2x128(low, high, 0x20).StoreUnsafe(ref destRef, (nuint)(2 * i));
            Avx.Permute2x128(low, high, 0x31).StoreUnsafe(ref destRef, (nuint)((2 * i) + 4));
        }

        return i;
    }

    /// <summary>
    /// オーバーサンプリング線形補間で変調を付与します。
    /// </summary>
    /// <param name="source">入力波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（0 で無変調）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
    /// <param name="amount">変調量（0 で無変調）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
        SimdMath.CopyComplexReals(source, real);
        var warped = ApplyOversampledLinear(real, sampleRate, amount, wowPhase, flutterPhase, oversample);
        var dst = new Complex[source.Length];
        SimdMath.WriteComplexReals(warped, dst);
        return dst;
    }

    /// <summary>
    /// 複素波形をオーバーサンプリング線形補間で逆補正します。
    /// </summary>
    /// <param name="warped">変調済み複素波形。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">変調量（送信時と同一値）。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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
        SimdMath.CopyComplexReals(warped, real);
        var corrected = CorrectOversampledLinear(real, sampleRate, amount, wowPhase, flutterPhase, oversample);
        var dst = new Complex[warped.Length];
        SimdMath.WriteComplexReals(corrected, dst);
        return dst;
    }

    /// <summary>
    /// 速度プロファイルから累積時間軸と正規化係数を算出します。
    /// </summary>
    /// <param name="speedProfile">サンプルごとの相対速度列。</param>
    /// <param name="cumul">累積時間軸（出力）。</param>
    /// <param name="scale">末尾を (n-1) に正規化する係数（出力）。</param>
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
    /// <param name="speedProfile">サンプルごとの相対速度列。</param>
    /// <param name="cumul">累積時間軸（出力）。</param>
    /// <param name="scale">末尾を (n-1) に正規化する係数（出力）。</param>
    private static void BuildCumul(double[] speedProfile, out double[] cumul, out double scale) =>
        BuildCumul((ReadOnlySpan<double>)speedProfile, out cumul, out scale);

    /// <summary>
    /// 線形補間で波形をオーバーサンプリングします。
    /// </summary>
    /// <param name="source">入力波形。</param>
    /// <param name="factor">オーバーサンプル倍率（1 以上）。</param>
    /// <returns>アップサンプル後の波形。</returns>
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
    /// <param name="samples">入力サンプル列。</param>
    /// <param name="position">連続インデックス位置（小数可）。</param>
    /// <returns>補間後のサンプル値。</returns>
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


