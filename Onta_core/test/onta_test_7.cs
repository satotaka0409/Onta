using Onta.Core;
using System.Numerics;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// 受信側耐性確認: ステレオ / 27サブキャリア / QPSK に対して
/// 5秒ごとのランダム位置で 5ms 無音化 + 0.5% ワウフラッター + 1% ホワイトノイズを付与します。
/// </summary>
public sealed class OntaTest7
{
    private const double WhiteNoiseLevel = 0.01;
    private const double WowFlutterAmount = 0.005;
    private const int SilenceIntervalSeconds = 5;
    private const int SilenceDurationMilliseconds = 5;
    private const int ImpairmentSeed = 20260910;

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 27,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Stereo);

    [Fact]
    public void Decode_MatchesOriginal_Stereo27ScQpsk_WithPeriodicRandomSilenceWowAndNoise()
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("Sample1_test7_rx_st27_qpsk.wav");
        var restoredPath = TestPaths.ResolveOutputPath("Sample1_test7_rx_st27_qpsk.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var fileInfo = new FileInfo(inputPath);

        var (leftSamples, rightSamples) = codec.EncodeFileToSamples(original, fileInfo);

        // 送信サンプルへ劣化を適用: 周期無音化 -> ワウ -> ノイズ。
        var leftF = ToFloat(leftSamples);
        var rightF = ToFloat(rightSamples.Length == 0 ? leftSamples : rightSamples);

        ApplyPeriodicRandomSilence(
            leftF,
            rightF,
            Profile.SampleRate,
            SilenceIntervalSeconds,
            SilenceDurationMilliseconds,
            ImpairmentSeed);

        var silencedLeft = ToComplex(leftF);
        var silencedRight = ToComplex(rightF);

        var (warpedLeft, warpedRight, wowPhase, flutterPhase) = NoisePlus.ApplyWowFlutterInMemory(
            silencedLeft,
            silencedRight,
            Profile.SampleRate,
            WowFlutterAmount,
            ImpairmentSeed);

        leftF = ToFloat(warpedLeft);
        rightF = ToFloat(warpedRight.Length == 0 ? warpedLeft : warpedRight);
        NormalizeToPeak(leftF, rightF, (float)Profile.SamplePeak);
        NoisePlus.AddWhiteNoiseInMemory(leftF, rightF, WhiteNoiseLevel, ImpairmentSeed);

        WavWriter.WriteStereo16(
            wavPath,
            Profile.SampleRate,
            ToComplex(leftF),
            ToComplex(rightF),
            Profile.SamplePeak);

        var decoded = codec.DecodeWavToFileBytes(
            wavPath,
            correctWow: false,
            wowParams: (WowFlutterAmount, wowPhase, flutterPhase));
        File.WriteAllBytes(restoredPath, decoded);

        Assert.Equal(original, decoded);
    }

    private static void ApplyPeriodicRandomSilence(
        Span<float> left,
        Span<float> right,
        int sampleRate,
        int intervalSeconds,
        int durationMilliseconds,
        int seed)
    {
        if (left.Length == 0)
        {
            return;
        }

        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left/right length mismatch.");
        }

        var intervalSamples = Math.Max(1, sampleRate * Math.Max(1, intervalSeconds));
        var silenceSamples = Math.Max(1, (sampleRate * Math.Max(1, durationMilliseconds)) / 1000);
        var random = new Random(seed);

        for (var windowStart = 0; windowStart < left.Length; windowStart += intervalSamples)
        {
            var windowEnd = Math.Min(windowStart + intervalSamples, left.Length);
            var latestStart = Math.Max(windowStart, windowEnd - silenceSamples);
            var span = Math.Max(1, latestStart - windowStart + 1);
            var start = windowStart + random.Next(span);
            var end = Math.Min(start + silenceSamples, windowEnd);
            for (var i = start; i < end; i++)
            {
                left[i] = 0f;
                right[i] = 0f;
            }
        }
    }

    private static void NormalizeToPeak(float[] left, float[] right, float peakTarget)
    {
        var peak = 0.0f;
        for (var i = 0; i < left.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(left[i]));
            peak = Math.Max(peak, Math.Abs(right[i]));
        }

        if (peak <= 0.0f)
        {
            return;
        }

        var scale = peakTarget / peak;
        for (var i = 0; i < left.Length; i++)
        {
            left[i] *= scale;
            right[i] *= scale;
        }
    }

    private static float[] ToFloat(Complex[] samples)
    {
        var dst = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            dst[i] = (float)samples[i].Real;
        }

        return dst;
    }

    private static Complex[] ToComplex(float[] samples)
    {
        var dst = new Complex[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            dst[i] = new Complex(samples[i], 0.0);
        }

        return dst;
    }
}
