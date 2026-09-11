using Onta.Core;
using System.Numerics;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ステレオ 18SC / QPSK の耐性テストです。
/// wow/flutter + 7kHz 低域通過近似 + ノイズ付与後の復元を検証します。
/// </summary>
public sealed class OntaTest8
{
    private const double WhiteNoiseLevel = 0.007;
    private const double WowFlutterAmount = 0.005;
    private const double LpfCutoffHz = 7000.0;
    private const int ImpairmentSeed = 20260911;

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 18,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Stereo);

    [Fact]
    public void Decode_MatchesOriginal_Stereo18ScQpsk_WithWowLpfAndNoise()
    {
        const string testTitle = "test8:" + nameof(Decode_MatchesOriginal_Stereo18ScQpsk_WithWowLpfAndNoise);
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("Sample1_test8_rx_st18_qpsk_lpf.wav");
        var restoredPath = TestPaths.ResolveOutputPath("Sample1_test8_rx_st18_qpsk_lpf.png");

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var fileInfo = new FileInfo(inputPath);

        var (leftSamples, rightSamples) = codec.EncodeFileToSamples(original, fileInfo);

        var leftRef = ToFloat(leftSamples);
        var rightRef = ToFloat(rightSamples.Length == 0 ? leftSamples : rightSamples);

        var (warpedLeft, warpedRight, wowPhase, flutterPhase) = NoisePlus.ApplyWowFlutterInMemory(
            leftSamples,
            rightSamples,
            Profile.SampleRate,
            WowFlutterAmount,
            ImpairmentSeed);

        var leftF = ToFloat(warpedLeft);
        var rightF = ToFloat(warpedRight.Length == 0 ? warpedLeft : warpedRight);

        ApplyLowPass7kHzApprox10dBPerOct(leftF, rightF, Profile.SampleRate);

        NormalizeToPeak(leftF, rightF, (float)Profile.SamplePeak);
        NoisePlus.AddWhiteNoiseInMemory(leftF, rightF, WhiteNoiseLevel, ImpairmentSeed);
        PrintChannelImpairmentRate(leftRef, rightRef, leftF, rightF, testTitle);

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

        PrintDecodeStageMetrics(codec.LastDecodeStageMetrics, testTitle);
        PrintBlockBitErrorRates(original, decoded, 4096, testTitle);
        Assert.Equal(original, decoded);
        HistoryAssert.SaveSendAndAssertRegistered(testTitle, inputPath, wavPath);
    }

    private static void ApplyLowPass7kHzApprox10dBPerOct(float[] left, float[] right, int sampleRate)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return;
        }

        if (left.Length != right.Length)
        {
            throw new ArgumentException("Left/right length mismatch.");
        }

        // 1次LPFを2段直列 + dry/wet 合成で約 -10dB/oct 相当を近似する。
        var dt = 1.0 / sampleRate;
        var rc = 1.0 / (2.0 * Math.PI * LpfCutoffHz);
        var alpha = dt / (rc + dt);
        const double wetMix = 0.85;
        const double dryMix = 1.0 - wetMix;

        static void FilterInPlace(float[] x, double alphaValue, double wet, double dry)
        {
            var y1 = (double)x[0];
            var y2 = (double)x[0];
            for (var i = 0; i < x.Length; i++)
            {
                y1 += alphaValue * (x[i] - y1);
                y2 += alphaValue * (y1 - y2);
                x[i] = (float)((dry * x[i]) + (wet * y2));
            }
        }

        FilterInPlace(left, alpha, wetMix, dryMix);
        FilterInPlace(right, alpha, wetMix, dryMix);
    }

    private static void PrintChannelImpairmentRate(
        float[] cleanLeft,
        float[] cleanRight,
        float[] impairedLeft,
        float[] impairedRight,
        string testTitle)
    {
        if (cleanLeft.Length != impairedLeft.Length || cleanRight.Length != impairedRight.Length)
        {
            throw new ArgumentException("Channel vectors must have identical lengths.");
        }

        const float changedThreshold = 1e-3f;
        long changed = 0;
        long total = 0;
        var mse = 0.0;

        for (var i = 0; i < cleanLeft.Length; i++)
        {
            var dl = impairedLeft[i] - cleanLeft[i];
            var dr = impairedRight[i] - cleanRight[i];
            mse += (dl * dl) + (dr * dr);
            if (Math.Abs(dl) > changedThreshold)
            {
                changed++;
            }

            if (Math.Abs(dr) > changedThreshold)
            {
                changed++;
            }

            total += 2;
        }

        var changedRate = total > 0 ? changed / (double)total : 0.0;
        var rmse = total > 0 ? Math.Sqrt(mse / total) : 0.0;
        Console.WriteLine($"[CHANNEL-ERR] test={testTitle} changed={changed} total={total} percent={changedRate * 100.0:F4}% threshold={changedThreshold:F4} rmse={rmse:F6}");
    }

    private static void PrintBlockBitErrorRates(byte[] original, byte[] decoded, int blockSize, string testTitle)
    {
        var blockCount = (original.Length + blockSize - 1) / blockSize;
        long totalBitErrors = 0;
        long totalBitsAllBlocks = 0;
        Console.WriteLine($"[DATA-BER] test={testTitle} start blocks={blockCount}");
        for (var block = 0; block < blockCount; block++)
        {
            var offset = block * blockSize;
            var len = Math.Min(blockSize, original.Length - offset);
            var bitErrors = 0;

            for (var i = 0; i < len; i++)
            {
                var diff = (byte)(original[offset + i] ^ decoded[offset + i]);
                bitErrors += CountSetBits(diff);
            }

            var totalBits = len * 8;
            var ber = totalBits > 0 ? bitErrors / (double)totalBits : 0.0;
            totalBitErrors += bitErrors;
            totalBitsAllBlocks += totalBits;
            Console.WriteLine($"[DATA-BER] test={testTitle} block={block} bytes={len} bitErrors={bitErrors} totalBits={totalBits} ber={ber:F8}");
        }

        var averageBer = totalBitsAllBlocks > 0 ? totalBitErrors / (double)totalBitsAllBlocks : 0.0;
        Console.WriteLine($"[DATA-BER] test={testTitle} average bitErrors={totalBitErrors} totalBits={totalBitsAllBlocks} ber={averageBer:F8} percent={averageBer * 100.0:F4}%");
    }

    private static void PrintDecodeStageMetrics(DecodeStageMetrics metrics, string testTitle)
    {
        var accepted = Math.Max(1, metrics.DataBlocksAccepted);
        var decoded = Math.Max(1, metrics.DataBlocksDecoded);
        var viterbiPercent = metrics.DataAcceptedViaViterbi * 100.0 / accepted;
        var turboPercent = metrics.DataAcceptedViaTurbo * 100.0 / accepted;
        var acceptPercent = metrics.DataBlocksAccepted * 100.0 / decoded;
        var attemptsPerBlock = metrics.DataBlocksDecoded > 0
            ? metrics.DataTotalAttempts / (double)metrics.DataBlocksDecoded
            : 0.0;
        Console.WriteLine(
            $"[DECODE-STAGE] test={testTitle} rsHeaderDecode={metrics.HeaderRsDecodeCount} dataDecoded={metrics.DataBlocksDecoded} dataAccepted={metrics.DataBlocksAccepted} acceptPercent={acceptPercent:F2}% viterbiAccepted={metrics.DataAcceptedViaViterbi} turboAccepted={metrics.DataAcceptedViaTurbo} fallbackUsed={metrics.DataFallbackUsed} attemptsPerBlock={attemptsPerBlock:F2}");
        Console.WriteLine(
            $"[DECODE-STAGE-RATE] test={testTitle} viterbiShare={viterbiPercent:F2}% turboShare={turboPercent:F2}%");
    }

    private static int CountSetBits(byte value)
    {
        var v = value;
        var count = 0;
        while (v != 0)
        {
            count += v & 1;
            v >>= 1;
        }

        return count;
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

