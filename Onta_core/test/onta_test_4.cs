using Onta.Core;
using System.Numerics;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// モノラル / 27サブキャリア / QPSK に、5% ホワイトノイズと 1% ワウフラッターを付与したラウンドトリップ試験です。
/// ワウはメモリ上の可逆写像で付与し、復号時に同一パラメータで逆補正します。
/// </summary>
public sealed class OntaTest4
{
    private const double WhiteNoiseLevel = 0.05;
    private const double WowFlutterAmount = 0.01;
    private const int ImpairmentSeed = 20260905;

    private static readonly FileWavCodecProfile Profile = new(
        FftSize: 128,
        CyclicPrefixLength: 32,
        ActiveSubcarriers: 27,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Mono);

    [Fact]
    public void EncodeDecode_QrPng_MatchesOriginal_Mono27ScQpsk_WithNoiseAndWowFlutter()
    {
        RoundTripWithImpairments(
            whiteNoiseLevel: WhiteNoiseLevel,
            wowAmount: WowFlutterAmount,
            wavName: "QR_326213_test4.wav",
            restoredName: "QR_326213_test4.png");
    }

    private void RoundTripWithImpairments(
        double whiteNoiseLevel,
        double wowAmount,
        string wavName,
        string restoredName)
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath(wavName);
        var restoredPath = TestPaths.ResolveOutputPath(restoredName);

        var original = File.ReadAllBytes(inputPath);
        var codec = new FileWavCodec(Profile);
        var fileInfo = new FileInfo(inputPath);

        var (leftSamples, rightSamples) = codec.EncodeFileToSamples(original, fileInfo);

        // 中間 WAV 量子化を挟まず、符号化サンプルへ直接ワウ→ノイズを付与する。
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
        var rightF = rightOut.Length == 0 ? Array.Empty<float>() : ToFloat(rightOut);
        // ピーク正規化後にノイズを載せる（正規化前だと小振幅 OFDM に対し実効 SNR が極端に悪化する）。
        if (rightF.Length == 0)
        {
            NormalizeToPeakMono(leftF, (float)Profile.SamplePeak);
        }
        else
        {
            NormalizeToPeak(leftF, rightF, (float)Profile.SamplePeak);
        }

        if (whiteNoiseLevel > 0.0)
        {
            NoisePlus.AddWhiteNoiseInMemory(leftF, rightF, whiteNoiseLevel, ImpairmentSeed);
        }

        WavWriter.WritePcm16(
            wavPath,
            Profile.SampleRate,
            ToComplex(leftF),
            rightF.Length == 0 ? Array.Empty<Complex>() : ToComplex(rightF),
            Profile.SamplePeak,
            Profile.ChannelMode);

        (double Amount, double WowPhase, double FlutterPhase)? wowParams =
            wowAmount > 0.0 ? (wowAmount, wowPhase, flutterPhase) : null;
        var decoded = codec.DecodeWavToFileBytes(
            wavPath,
            correctWow: false,
            wowParams: wowParams);
        File.WriteAllBytes(restoredPath, decoded);

        PrintBlockBitErrorRates(original, decoded, 4096);
        Assert.Equal(original, decoded);
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

    private static void NormalizeToPeakMono(float[] samples, float peakTarget)
    {
        var peak = 0.0f;
        for (var i = 0; i < samples.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(samples[i]));
        }

        if (peak <= 0.0f)
        {
            return;
        }

        var scale = peakTarget / peak;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= scale;
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

    private static void PrintBlockBitErrorRates(byte[] original, byte[] decoded, int blockSize)
    {
        var blockCount = (original.Length + blockSize - 1) / blockSize;
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
            Console.WriteLine($"[DATA-BER] block={block} bytes={len} bitErrors={bitErrors} totalBits={totalBits} ber={ber:F8}");
        }
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
