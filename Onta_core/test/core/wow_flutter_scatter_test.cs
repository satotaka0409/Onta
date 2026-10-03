using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

public sealed class WowFlutterScatterTests
{
    /// <summary>
    /// 区間並列（SIMD）・区間スキップ付きの散乱 Correct が、逐次計算の参照実装と完全に一致することを確かめます。
    /// </summary>
    [Theory]
    [InlineData(0.001, 30_000, 44_100)]
    [InlineData(0.005, 1_000, 88_200)]
    [InlineData(0.01, 60_000, 20_000)]
    [InlineData(0.003, 0, 13_230)]
    [InlineData(0.005, 150_000, 49_000)]
    public void CorrectPrefix_MatchesSequentialReference(double amount, int prefixStart, int prefixLength)
    {
        const int SampleRate = 44100;
        var rng = new Random(3);
        var warped = new Complex[200_000];
        for (var i = 0; i < warped.Length; i++)
        {
            warped[i] = new Complex((rng.NextDouble() * 2) - 1, 0);
        }

        for (var trial = 0; trial < 4; trial++)
        {
            var wowPhase = rng.NextDouble() * 2 * Math.PI;
            var flutterPhase = rng.NextDouble() * 2 * Math.PI;
            var length = Math.Min(prefixLength, warped.Length - prefixStart);
            var actual = new Complex[length];
            WowFlutterWarp.CorrectPrefixWithReferenceLength(
                warped, warped.Length, prefixStart, length, SampleRate, amount, wowPhase, flutterPhase, actual);
            var expected = ReferenceCorrectPrefix(
                warped, warped.Length, prefixStart, length, SampleRate, amount, wowPhase, flutterPhase);

            for (var j = 0; j < length; j++)
            {
                Assert.True(
                    expected[j] == actual[j].Real,
                    $"trial={trial} j={j} expected={expected[j]:R} actual={actual[j].Real:R}");
            }
        }
    }

    /// <summary>
    /// 平均化と相関を 1 パスにまとめた CorrectPrefixCorrelateReal が、逐次 Correct → CorrelateDenseReals と完全に一致することを確かめます。
    /// </summary>
    [Theory]
    [InlineData(0.005, 30_000, 44_100)]
    [InlineData(0.01, 1_000, 88_203)]
    [InlineData(0.002, 0, 13_230)]
    public void CorrectPrefixCorrelateReal_MatchesSequentialReference(double amount, int prefixStart, int prefixLength)
    {
        const int SampleRate = 44100;
        var rng = new Random(11);
        var warped = new Complex[200_000];
        for (var i = 0; i < warped.Length; i++)
        {
            warped[i] = new Complex((rng.NextDouble() * 2) - 1, 0);
        }

        var ideal = new double[prefixLength];
        for (var i = 0; i < ideal.Length; i++)
        {
            ideal[i] = Math.Sin(i * 0.031) + (0.3 * Math.Sin(i * 0.27));
        }

        var meanIdeal = ideal.Average();
        var energyIdeal = ideal.Sum(v => (v - meanIdeal) * (v - meanIdeal));
        var sumScratch = new double[prefixLength];
        var countScratch = new int[prefixLength];
        for (var trial = 0; trial < 4; trial++)
        {
            var wowPhase = rng.NextDouble() * 2 * Math.PI;
            var flutterPhase = rng.NextDouble() * 2 * Math.PI;
            var actual = WowFlutterWarp.CorrectPrefixCorrelateReal(
                warped,
                warped.Length,
                prefixStart,
                prefixLength,
                SampleRate,
                amount,
                wowPhase,
                flutterPhase,
                ideal,
                meanIdeal,
                energyIdeal,
                sumScratch,
                countScratch);
            var corrected = ReferenceCorrectPrefix(
                warped, warped.Length, prefixStart, prefixLength, SampleRate, amount, wowPhase, flutterPhase);
            var expected = WowFlutterWarp.CorrelateDenseReals(corrected, ideal, meanIdeal, energyIdeal);
            Assert.True(expected == actual, $"trial={trial} expected={expected:R} actual={actual:R}");
        }
    }

    /// <summary>
    /// 区間並列（SIMD）の BuildSourceIndexMap が、逐次計算の参照実装と完全に一致することを確かめます。
    /// </summary>
    [Theory]
    [InlineData(1, 0.005)]
    [InlineData(511, 0.005)]
    [InlineData(2048, 0.003)]
    [InlineData(2049, 0.005)]
    [InlineData(100_000, 0.005)]
    [InlineData(441_000, 0.01)]
    [InlineData(441_000, 0.0)]
    public void BuildSourceIndexMap_MatchesSequentialReference(int sampleCount, double amount)
    {
        const int SampleRate = 44100;
        var rng = new Random(sampleCount);
        for (var trial = 0; trial < 3; trial++)
        {
            var wowPhase = trial == 0 ? 0.0 : rng.NextDouble() * 2 * Math.PI;
            var flutterPhase = rng.NextDouble() * 2 * Math.PI;
            var actual = WowFlutterWarp.BuildSourceIndexMap(sampleCount, SampleRate, amount, wowPhase, flutterPhase);
            var expected = ReferenceBuildSourceIndexMap(sampleCount, SampleRate, amount, wowPhase, flutterPhase);
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>
    /// 複素版 Correct が、参照の写像表とスカラの平均化で求めた値と完全に一致することを確かめます。
    /// </summary>
    [Theory]
    [InlineData(3, 0.005)]
    [InlineData(100_003, 0.005)]
    [InlineData(441_000, 0.01)]
    public void Correct_MatchesSequentialReference(int sampleCount, double amount)
    {
        const int SampleRate = 44100;
        var rng = new Random(sampleCount + 7);
        var warped = new Complex[sampleCount];
        for (var i = 0; i < warped.Length; i++)
        {
            warped[i] = new Complex((rng.NextDouble() * 2) - 1, rng.NextDouble());
        }

        var wowPhase = rng.NextDouble() * 2 * Math.PI;
        var flutterPhase = rng.NextDouble() * 2 * Math.PI;
        var actual = WowFlutterWarp.Correct(warped, SampleRate, amount, wowPhase, flutterPhase);
        var map = ReferenceBuildSourceIndexMap(sampleCount, SampleRate, amount, wowPhase, flutterPhase);
        var sum = new double[sampleCount];
        var count = new int[sampleCount];
        for (var i = 0; i < sampleCount; i++)
        {
            sum[map[i]] += warped[i].Real;
            count[map[i]]++;
        }

        for (var i = 0; i < sampleCount; i++)
        {
            var expected = sum[i] / count[i];
            Assert.True(
                expected.Equals(actual[i].Real) && actual[i].Imaginary == 0.0,
                $"i={i} expected={expected:R} actual={actual[i]}");
        }
    }

    /// <summary>
    /// 区間並列化前の逐次 BuildSourceIndexMap です。
    /// </summary>
    private static int[] ReferenceBuildSourceIndexMap(
        int sampleCount,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        var wowOmega = 2.0 * Math.PI * WowFlutterWarp.WowFrequencyHz / sampleRate;
        var flutterOmega = 2.0 * Math.PI * WowFlutterWarp.FlutterFrequencyHz / sampleRate;
        var wowGain = amount * 0.65;
        var flutterGain = amount * 0.35;

        double CumulAt(int i) => i <= 0
            ? 0.0
            : i + (wowGain * SumOfSines(wowPhase, wowOmega, i)) + (flutterGain * SumOfSines(flutterPhase, flutterOmega, i));

        var count = new int[sampleCount];
        var cumul = new double[sampleCount];
        var last = sampleCount - 1;
        var cumulEnd = CumulAt(last);
        var scale = cumulEnd > 1e-12 ? last / cumulEnd : 1.0;
        var map = new int[sampleCount];
        var cosWowStep = Math.Cos(wowOmega);
        var sinWowStep = Math.Sin(wowOmega);
        var cosFlutterStep = Math.Cos(flutterOmega);
        var sinFlutterStep = Math.Sin(flutterOmega);
        var wowSin = Math.Sin(wowPhase);
        var wowCos = Math.Cos(wowPhase);
        var flutterSin = Math.Sin(flutterPhase);
        var flutterCos = Math.Cos(flutterPhase);
        var c = 0.0;
        var untilReanchor = 512;
        for (var i = 0; i < sampleCount; i++)
        {
            cumul[i] = c;
            var idx = (int)((c * scale) + 0.5);
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
                untilReanchor = 512;
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

        var donorCursor = 0;
        foreach (var target in missing)
        {
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
                bestI = Math.Clamp((int)((target / Math.Max(scale, 1e-12)) + 0.5), 0, sampleCount - 1);
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

            count[map[bestI]]--;
            map[bestI] = target;
            count[target]++;
        }

        return map;
    }

    /// <summary>
    /// 区間並列化前の逐次散乱 Correct（512 サンプルごとに解析角へ再同期）です。
    /// </summary>
    private static double[] ReferenceCorrectPrefix(
        Complex[] warped,
        int referenceLength,
        int prefixStart,
        int prefixLength,
        int sampleRate,
        double amount,
        double wowPhase,
        double flutterPhase)
    {
        var sum = new double[prefixLength];
        var count = new int[prefixLength];
        referenceLength = Math.Max(referenceLength, prefixStart + prefixLength);
        referenceLength = Math.Max(referenceLength, 2);
        var wowOmega = 2.0 * Math.PI * WowFlutterWarp.WowFrequencyHz / sampleRate;
        var flutterOmega = 2.0 * Math.PI * WowFlutterWarp.FlutterFrequencyHz / sampleRate;
        var wowGain = amount * 0.65;
        var flutterGain = amount * 0.35;

        double CumulAt(int i) => i <= 0
            ? 0.0
            : i + (wowGain * SumOfSines(wowPhase, wowOmega, i)) + (flutterGain * SumOfSines(flutterPhase, flutterOmega, i));

        var absEnd = referenceLength - 1;
        var cumulEnd = CumulAt(absEnd);
        var scale = cumulEnd > 1e-12 ? absEnd / cumulEnd : 1.0;
        var prefixEnd = prefixStart + prefixLength;
        var slack = Math.Max(sampleRate / 5, (int)(prefixLength * 0.05) + 64);
        var iMin = Math.Max(0, prefixStart - slack);
        var iMax = Math.Min(warped.Length, prefixEnd + slack);

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
        var untilReanchor = 512;
        for (var i = iMin; i < iMax; i++)
        {
            var src = (int)((cumul * scale) + 0.5);
            if ((uint)(src - prefixStart) < (uint)prefixLength)
            {
                sum[src - prefixStart] += warped[i].Real;
                count[src - prefixStart]++;
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
                untilReanchor = 512;
                wowAngle = wowPhase + ((i + 1) * wowOmega);
                flutterAngle = flutterPhase + ((i + 1) * flutterOmega);
                wowSin = Math.Sin(wowAngle);
                wowCos = Math.Cos(wowAngle);
                flutterSin = Math.Sin(flutterAngle);
                flutterCos = Math.Cos(flutterAngle);
            }
        }

        var result = new double[prefixLength];
        for (var j = 0; j < prefixLength; j++)
        {
            result[j] = count[j] > 0
                ? sum[j] / count[j]
                : warped[Math.Clamp(prefixStart + j, 0, warped.Length - 1)].Real;
        }

        return result;
    }

    /// <summary>
    /// sin(phase0 + k·omega) の k=0..count-1 の和（閉形式）です。
    /// </summary>
    private static double SumOfSines(double phase0, double omega, int count)
    {
        var half = omega * 0.5;
        var denom = Math.Sin(half);
        if (Math.Abs(denom) < 1e-12)
        {
            return count * Math.Sin(phase0);
        }

        return Math.Sin(count * half) / denom * Math.Sin(phase0 + ((count - 1) * half));
    }
}
