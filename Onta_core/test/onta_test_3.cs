using Onta.Core;
using System.Numerics;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ステレオ 18SC / 16QAM の劣化耐性往復テストです。
/// ホワイトノイズと wow/flutter を付与して復元可否を検証します。
/// </summary>
public sealed class OntaTest3
{
    private const double WhiteNoiseLevel = 0.02;
    private const double WowFlutterAmount = 0.01;
    private const int ImpairmentSeed = 20260904;

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 18,
        ModulationScheme: ModulationScheme.Qam16);

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WowOnly()
    {
        // wow/flutter のみを付与した条件で往復確認する。
        RoundTripWithImpairments(
            whiteNoiseLevel: 0.0,
            wowAmount: WowFlutterAmount,
            wavName: "Sample1_test3_wow_only.wav",
            restoredName: "Sample1_test3_wow_only.png",
            testTitle: "test3:" + nameof(EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WowOnly));
    }

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WithNoiseAndWowFlutter()
    {
        // wow/flutter に加えて 2% AWGN を重畳する。
        RoundTripWithImpairments(
            whiteNoiseLevel: WhiteNoiseLevel,
            wowAmount: WowFlutterAmount,
            wavName: "Sample1_test3.wav",
            restoredName: "Sample1_test3.png",
            testTitle: "test3:" + nameof(EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WithNoiseAndWowFlutter));
    }

    private void RoundTripWithImpairments(
        double whiteNoiseLevel,
        double wowAmount,
        string wavName,
        string restoredName,
        string testTitle)
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath(wavName);
        var restoredPath = TestPaths.ResolveOutputPath(restoredName);

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var fileInfo = new FileInfo(inputPath);

        var (leftSamples, rightSamples) = codec.EncodeFileToSamples(original, fileInfo);
        var leftRef = ToFloat(leftSamples);
        var rightRef = ToFloat(rightSamples.Length == 0 ? leftSamples : rightSamples);

        // 中間WAVを作る前にサンプルへ劣化を適用する。
        double wowPhase = 0.0;
        double flutterPhase = 0.0;
        Complex[] leftOut = leftSamples;
        Complex[] rightOut = rightSamples;
        if (wowAmount > 0.0)
        {
            (leftOut, rightOut, wowPhase, flutterPhase) = NoisePlus.ApplyWowFlutterInMemory(
                leftSamples,
                rightSamples,
                Profile.SampleRate,
                wowAmount,
                ImpairmentSeed);
        }

        var leftF = ToFloat(leftOut);
        var rightF = ToFloat(rightOut.Length == 0 ? leftOut : rightOut);
        // 正規化後にノイズを重畳して受信側SNRを下げる。
        NormalizeToPeak(leftF, rightF, (float)Profile.SamplePeak);
        if (whiteNoiseLevel > 0.0)
        {
            NoisePlus.AddWhiteNoiseInMemory(leftF, rightF, whiteNoiseLevel, ImpairmentSeed);
        }
        PrintChannelImpairmentRate(leftRef, rightRef, leftF, rightF, testTitle);

        WavWriter.WriteStereo16(
            wavPath,
            Profile.SampleRate,
            ToComplex(leftF),
            ToComplex(rightF),
            Profile.SamplePeak);

        (double Amount, double WowPhase, double FlutterPhase)? wowParams =
            wowAmount > 0.0 ? (wowAmount, wowPhase, flutterPhase) : null;
        var decoded = codec.DecodeWavToFileBytes(
            wavPath,
            correctWow: false,
            wowParams: wowParams);
        File.WriteAllBytes(restoredPath, decoded);

        PrintDecodeStageMetrics(codec.LastDecodeStageMetrics, testTitle);
        PrintBlockBitErrorRates(original, decoded, 4096, testTitle);
        Assert.Equal(original, decoded);
        HistoryAssert.SaveSendAndAssertRegistered(testTitle, inputPath, wavPath);
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

    [Fact]
    public void WowFlutterWarp_ApplyCorrect_RoundTripsNearExact()
    {
        const int n = 44100;
        const int sampleRate = 44100;
        const double amount = 0.01;
        var (wowPhase, flutterPhase) = WowFlutterWarp.CreatePhases(ImpairmentSeed);
        var src = new double[n];
        for (var i = 0; i < n; i++)
        {
            src[i] = Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate);
        }

        // 最近傍補間は高精度で往復できることを確認する。
        var warpedNn = WowFlutterWarp.Apply(src, sampleRate, amount, wowPhase, flutterPhase);
        var restoredNn = WowFlutterWarp.Correct(warpedNn, sampleRate, amount, wowPhase, flutterPhase);
        Assert.True(Mse(src, restoredNn) < 1e-30, $"nearest MSE={Mse(src, restoredNn):E4}");

        // オーバーサンプル線形補間も許容誤差内で復元できることを確認する。
        var warpedLin = WowFlutterWarp.ApplyOversampledLinear(
            src, sampleRate, amount, wowPhase, flutterPhase, oversample: 8);
        var restoredLin = WowFlutterWarp.CorrectOversampledLinear(
            warpedLin, sampleRate, amount, wowPhase, flutterPhase, oversample: 8);
        Assert.True(Mse(src, restoredLin) < 1e-4, $"oversampled MSE={Mse(src, restoredLin):E4}");
    }

    private static double Mse(double[] a, double[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < a.Length; i++)
        {
            var e = a[i] - b[i];
            sum += e * e;
        }

        return sum / a.Length;
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
}

