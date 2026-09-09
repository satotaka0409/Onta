using System.Buffers;
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
    /// <param name="seed">乱数シード。0 のときは非シード Random。</param>
    /// <returns>wow 位相と flutter 位相（ラジアン）の組。</returns>
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
    /// <param name="sampleCount">プロファイル長（サンプル数）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <returns>サンプルごとの相対速度配列。</returns>
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
    /// 速度プロファイル配列へ wow/flutter 変調を書き込みます。
    /// </summary>
    /// <param name="profile">書き込み先の速度プロファイル。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
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

        var sinWow = Math.Sin(wowPhase);
        var cosWow = Math.Cos(wowPhase);
        var sinFlutter = Math.Sin(flutterPhase);
        var cosFlutter = Math.Cos(flutterPhase);
        var sinWowStep = Math.Sin(wowStep);
        var cosWowStep = Math.Cos(wowStep);
        var sinFlutterStep = Math.Sin(flutterStep);
        var cosFlutterStep = Math.Cos(flutterStep);

        for (var i = 0; i < profile.Length; i++)
        {
            var modulation = (0.65 * sinWow) + (0.35 * sinFlutter);
            var speed = 1.0 + (amount * modulation);
            profile[i] = speed < 0.05 ? 0.05 : speed;

            var nextSinWow = (sinWow * cosWowStep) + (cosWow * sinWowStep);
            var nextCosWow = (cosWow * cosWowStep) - (sinWow * sinWowStep);
            sinWow = nextSinWow;
            cosWow = nextCosWow;

            var nextSinFlutter = (sinFlutter * cosFlutterStep) + (cosFlutter * sinFlutterStep);
            var nextCosFlutter = (cosFlutter * cosFlutterStep) - (sinFlutter * sinFlutterStep);
            sinFlutter = nextSinFlutter;
            cosFlutter = nextCosFlutter;
        }
    }

    /// <summary>
    /// 終端正規化付き累積と、出力→入力の最近傍インデックス写像を構築します。
    /// 写像は全ソース添え字を少なくとも1回含むよう補正し、gather 逆変換が穴なしで復元できるようにします。
    /// </summary>
    /// <param name="sampleCount">サンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <returns>出力 index → 入力 index の写像。長さ 0 なら空配列。</returns>
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

        var profile = ArrayPool<double>.Shared.Rent(sampleCount);
        var count = ArrayPool<int>.Shared.Rent(sampleCount);
        try
        {
            FillSpeedProfile(profile.AsSpan(0, sampleCount), sampleRate, amount, wowPhase, flutterPhase);
            BuildCumul(profile.AsSpan(0, sampleCount), out var cumul, out var scale);
            var map = new int[sampleCount];
            var last = sampleCount - 1;
            Array.Clear(count, 0, sampleCount);
            for (var i = 0; i < sampleCount; i++)
            {
                var srcPos = cumul[i] * scale;
                var idx = (int)Math.Round(srcPos, MidpointRounding.AwayFromZero);
                map[i] = Math.Clamp(idx, 0, last);
                count[map[i]]++;
            }

            // 未参照ソースを、重複参照している出力スロットへ再割当て（全射化）。
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
        finally
        {
            ArrayPool<double>.Shared.Return(profile);
            ArrayPool<int>.Shared.Return(count);
        }
    }

    /// <summary>
    /// 可逆ワウ付与（最近傍写像）。double 精度の実信号向けです。
    /// </summary>
    /// <param name="source">入力実信号。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <returns>ワウ付与後の実信号。</returns>
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
    /// <param name="warped">ワウ付与済み実信号。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <returns>逆補正後の実信号。</returns>
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
    /// 複素 OFDM 実信号（Imag≈0）へ可逆ワウを付与します。
    /// </summary>
    /// <param name="source">入力複素サンプル（実部のみ使用）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <returns>ワウ付与後の複素サンプル（Imag=0）。</returns>
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
    /// 複素 OFDM 実信号の可逆ワウ逆補正です。
    /// </summary>
    /// <param name="warped">ワウ付与済み複素サンプル。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <returns>逆補正後の複素サンプル（Imag=0）。</returns>
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
    /// 複素 OFDM 実信号の可逆ワウ逆補正を in-place で行います（一時バッファはプール）。
    /// </summary>
    /// <param name="warped">補正対象の複素配列（全体を in-place 更新）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
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
    /// 先頭 <paramref name="length"/> サンプルだけを in-place 補正します（プール配列の部分利用向け）。
    /// </summary>
    /// <param name="warped">補正対象の複素配列。</param>
    /// <param name="length">先頭から補正するサンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
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
    /// 複素配列全体を gather 逆補正して <paramref name="destination"/> へ書き込みます。
    /// </summary>
    /// <param name="warped">ワウ付与済み複素サンプル。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="destination">逆補正結果の書き込み先。</param>
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
    /// 先頭 <paramref name="length"/> サンプルを gather 逆補正して書き込みます。
    /// </summary>
    /// <param name="warped">ワウ付与済み複素サンプル。</param>
    /// <param name="length">先頭から補正するサンプル数。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="destination">逆補正結果の書き込み先（in-place 可）。</param>
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

            // destination == warped のときも安全なよう、先に sum へ集約済み。
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
    /// 線形補間＋ソース側オーバーサンプルによる低残差ワウ付与です。
    /// 出力長は入力と同じで、読み出しだけを高分解能化します。
    /// </summary>
    /// <param name="source">入力実信号。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="oversample">ソース側オーバーサンプル倍率。</param>
    /// <returns>ワウ付与後の実信号。</returns>
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
    /// <param name="warped">ワウ付与済み実信号。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="oversample">ワープ側オーバーサンプル倍率。</param>
    /// <returns>逆補正後の実信号。</returns>
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
    /// 複素 OFDM 実信号へ線形補間オーバーサンプルワウを付与します。
    /// </summary>
    /// <param name="source">入力複素サンプル（実部のみ使用）。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="oversample">ソース側オーバーサンプル倍率。</param>
    /// <returns>ワウ付与後の複素サンプル（Imag=0）。</returns>
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
    /// 複素 OFDM 実信号の線形補間オーバーサンプルワウ逆補正です。
    /// </summary>
    /// <param name="warped">ワウ付与済み複素サンプル。</param>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    /// <param name="amount">速度変調の振幅。</param>
    /// <param name="wowPhase">wow 初期位相（ラジアン）。</param>
    /// <param name="flutterPhase">flutter 初期位相（ラジアン）。</param>
    /// <param name="oversample">ワープ側オーバーサンプル倍率。</param>
    /// <returns>逆補正後の複素サンプル（Imag=0）。</returns>
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
    /// 速度プロファイルから終端正規化付き累積時間とスケールを構築します。
    /// </summary>
    /// <param name="speedProfile">サンプル単位の相対速度。</param>
    /// <param name="cumul">終端正規化前の累積時間配列。</param>
    /// <param name="scale">終端をサンプル長に合わせるスケール。</param>
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
    /// <see cref="BuildCumul(ReadOnlySpan{double}, out double[], out double)"/> の配列引数版です。
    /// </summary>
    /// <param name="speedProfile">サンプル単位の相対速度。</param>
    /// <param name="cumul">終端正規化前の累積時間配列。</param>
    /// <param name="scale">終端をサンプル長に合わせるスケール。</param>
    private static void BuildCumul(double[] speedProfile, out double[] cumul, out double scale) =>
        BuildCumul((ReadOnlySpan<double>)speedProfile, out cumul, out scale);

    /// <summary>
    /// 線形補間で <paramref name="factor"/> 倍にアップサンプルします。
    /// </summary>
    /// <param name="source">入力実信号。</param>
    /// <param name="factor">アップサンプル倍率。</param>
    /// <returns>アップサンプル後の実信号。</returns>
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
    /// 連続位置での線形補間サンプル値を返します。
    /// </summary>
    /// <param name="samples">補間対象のサンプル列。</param>
    /// <param name="position">連続位置（サンプル単位）。</param>
    /// <returns>線形補間したサンプル値。</returns>
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

