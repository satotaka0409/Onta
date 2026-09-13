using System.Numerics;
using System.Reflection;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ブロックヘッダー起点の復号パラメータ選択を検証するテストです。
/// </summary>
public sealed class OntaTest6
{
    [Fact]
    public void Decode_UsesBlockHeaderModulationMode_InsteadOfReceiverProfile()
    {
        const string testTitle = "test6:" + nameof(Decode_UsesBlockHeaderModulationMode_InsteadOfReceiverProfile);
        var txProfile = new FileWavCodecProfile(
            ActiveSubcarriers: 32,
            ModulationScheme: ModulationScheme.Qam16,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);
        // 受信プロファイルを意図的に不一致にしても、ヘッダー情報で復号できることを確認する。
        var rxProfile = new FileWavCodecProfile(
            ActiveSubcarriers: 8,
            ModulationScheme: ModulationScheme.Qam64,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);

        var txCodec = new FileWavCodec(txProfile);
        var rxCodec = new FileWavCodec(rxProfile);
        var inputPath = TestPaths.ResolveInputPng();
        var inputInfo = new FileInfo(inputPath);
        var outputPath = TestPaths.ResolveOutputPath("Sample1_test6_from_samples.wav");
        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 37) & 0xFF);
        }

        var (left, right) = txCodec.EncodeFileToSamples(payload, inputInfo);
        var decoded = rxCodec.DecodePcmSamplesToFileBytes(left, right, correctWow: false);

        PrintDecodeStageMetrics(rxCodec.LastDecodeStageMetrics, testTitle);

        Assert.Equal(payload, decoded);
        HistoryAssert.SaveSendAndAssertRegistered(testTitle, inputPath, outputPath);
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
    public void HeaderOfdm_IsMonoEvenWhenProfileIsStereo()
    {
        var codec = new FileWavCodec(new FileWavCodecProfile(
            ActiveSubcarriers: 32,
            ModulationScheme: ModulationScheme.Qam64,
            ChannelMode: ChannelMode.Stereo));

        var method = typeof(FileWavCodec).GetMethod(
            "CreateHeaderOfdm",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var headerOfdm = (OfdmGenerator?)method!.Invoke(codec, [null]);
        Assert.NotNull(headerOfdm);
        Assert.Equal(ChannelMode.Mono, headerOfdm!.ChannelMode);
    }

    [Fact]
    public void StereoProfile_HeaderPreambleWaveform_IsIdenticalOnLeftAndRight()
    {
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 32,
            ModulationScheme: ModulationScheme.Qam64,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);

        var codec = new FileWavCodec(profile);
        var inputInfo = new FileInfo(TestPaths.ResolveInputPng());
        var (left, right) = codec.EncodeFileToSamples([0x5A], inputInfo);

        var compareSamples = profile.LeadingSilenceSamples + profile.UnmodulatedPreambleSamples;
        Assert.True(left.Length > compareSamples);
        Assert.Equal(left.Length, right.Length);
        for (var i = 0; i < compareSamples; i++)
        {
            Assert.InRange(Math.Abs(left[i].Real - right[i].Real), 0.0, 1e-12);
            Assert.InRange(Math.Abs(left[i].Imaginary - right[i].Imaginary), 0.0, 1e-12);
        }
    }

    [Fact]
    public void GroupD_Downgrade_AdjustsBitsPerOfdmSymbol_For32Sc64Qam()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(32, ChannelMode.Mono),
            activeSubcarriers: 32,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qam64,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: false,
            pilotSpacing: 8,
            randomSeed: 7,
            carrierGrid: OfdmCarrierGrid.Sc24Family);

        var ofdm = new OfdmGenerator(config);

        // 32SC: 8 pilot + 24 data。Group D の6本は16QAMに落として 132bit/symbol。
        Assert.Equal(132, ofdm.BitsPerOfdmSymbol);
    }

    [Fact]
    public void SampleCountForBitCount_UsesDowngradedGroupDCapacity()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(32, ChannelMode.Mono),
            activeSubcarriers: 32,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qam64,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: false,
            pilotSpacing: 8,
            randomSeed: 8,
            carrierGrid: OfdmCarrierGrid.Sc24Family);

        var ofdm = new OfdmGenerator(config);

        // 265bit は 132bit/symbol なら 3 symbol 必要（144bit/symbol 前提なら 2）。
        Assert.Equal(3 * ofdm.SamplesPerOfdmSymbol, ofdm.SampleCountForBitCount(265));
    }

    [Fact]
    public void PilotEqualizer_IsClosedWithinEachPilotVoronoiCell()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(16, ChannelMode.Mono),
            activeSubcarriers: 16,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: false,
            pilotSpacing: 8,
            randomSeed: 1,
            carrierGrid: OfdmCarrierGrid.Sc8Family);
        var ofdm = new OfdmGenerator(config);

        var allCarriers = GetPrivateField<List<int>>(ofdm, "_leftAllCarrierBins");
        var pilotBins = GetPrivateField<List<int>>(ofdm, "_leftPilotBins").OrderBy(x => x).ToList();
        // 16SC = 2グループ × CH1/CH5 = 4パイロット
        Assert.Equal(4, pilotBins.Count);

        var pilotGains = new[] { 0.5, 2.0, 0.7, 1.6 };
        var freqBins = new Complex[config.FftSize];
        for (var i = 0; i < pilotBins.Count; i++)
        {
            freqBins[pilotBins[i]] = new Complex(pilotGains[i], 0.0);
        }

        var estimateMethod = typeof(OfdmGenerator).GetMethod(
            "EstimatePilotEqualizers",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(estimateMethod);

        var equalizers = (Complex[]?)estimateMethod!.Invoke(ofdm, [freqBins, pilotBins, false, null]);
        Assert.NotNull(equalizers);

        var groupMethod = typeof(OfdmGenerator).GetMethod(
            "ResolvePilotGroupIndex",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(groupMethod);

        Assert.True((equalizers![pilotBins[0]] - equalizers[pilotBins[1]]).Magnitude > 0.3);

        foreach (var carrier in allCarriers)
        {
            var group = (int)groupMethod!.Invoke(null, [carrier, pilotBins])!;
            Assert.InRange(group, 0, pilotBins.Count - 1);
            var expected = equalizers[pilotBins[group]];
            Assert.InRange((equalizers[carrier] - expected).Magnitude, 0.0, 1e-9);
        }
    }

    [Fact]
    public void PilotEqualizer_IsClosedWithinEachPilotVoronoiCell_ForStereoLeftAndRight()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(16, ChannelMode.Stereo),
            activeSubcarriers: 16,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Stereo,
            enableFrequencyInterleaving: false,
            pilotSpacing: 8,
            stereoFrequencyShiftBins: 1,
            randomSeed: 2,
            carrierGrid: OfdmCarrierGrid.Sc8Family);
        var ofdm = new OfdmGenerator(config);

        var leftCarriers = GetPrivateField<List<int>>(ofdm, "_leftAllCarrierBins");
        var rightCarriers = GetPrivateField<List<int>>(ofdm, "_rightAllCarrierBins");
        var leftPilots = GetPrivateField<List<int>>(ofdm, "_leftPilotBins").OrderBy(x => x).ToList();
        var rightPilots = GetPrivateField<List<int>>(ofdm, "_rightPilotBins").OrderBy(x => x).ToList();
        Assert.Equal(4, leftPilots.Count);
        Assert.Equal(4, rightPilots.Count);

        var estimateMethod = typeof(OfdmGenerator).GetMethod(
            "EstimatePilotEqualizers",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(estimateMethod);

        var groupMethod = typeof(OfdmGenerator).GetMethod(
            "ResolvePilotGroupIndex",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(groupMethod);

        var leftGains = new[] { 0.6, 1.8, 0.5, 1.4 };
        var leftFreqBins = new Complex[config.FftSize];
        for (var i = 0; i < leftPilots.Count; i++)
        {
            leftFreqBins[leftPilots[i]] = new Complex(leftGains[i], 0.0);
        }

        var leftEqualizers = (Complex[]?)estimateMethod!.Invoke(ofdm, [leftFreqBins, leftPilots, false, null]);
        Assert.NotNull(leftEqualizers);

        Assert.True((leftEqualizers![leftPilots[0]] - leftEqualizers[leftPilots[1]]).Magnitude > 0.2);
        foreach (var carrier in leftCarriers)
        {
            var group = (int)groupMethod!.Invoke(null, [carrier, leftPilots])!;
            var expected = leftEqualizers[leftPilots[group]];
            Assert.InRange((leftEqualizers[carrier] - expected).Magnitude, 0.0, 1e-9);
        }

        var rightGains = new[] { 1.4, 0.4, 1.7, 0.55 };
        var rightFreqBins = new Complex[config.FftSize];
        for (var i = 0; i < rightPilots.Count; i++)
        {
            rightFreqBins[rightPilots[i]] = new Complex(rightGains[i], 0.0);
        }

        var rightEqualizers = (Complex[]?)estimateMethod.Invoke(ofdm, [rightFreqBins, rightPilots, true, null]);
        Assert.NotNull(rightEqualizers);

        Assert.True((rightEqualizers![rightPilots[0]] - rightEqualizers[rightPilots[1]]).Magnitude > 0.2);
        foreach (var carrier in rightCarriers)
        {
            var group = (int)groupMethod.Invoke(null, [carrier, rightPilots])!;
            var expected = rightEqualizers[rightPilots[group]];
            Assert.InRange((rightEqualizers[carrier] - expected).Magnitude, 0.0, 1e-9);
        }

        Assert.True((leftEqualizers[leftPilots[0]] - rightEqualizers[rightPilots[0]]).Magnitude > 0.01);
    }

    [Fact]
    public void FrequencyInterleavePermutation_ChangesEveryOfdmSymbol()
    {
        var config = new OfdmConfig(
            fftSize: 64,
            activeSubcarriers: 16,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: true,
            pilotSpacing: 8,
            sampleRate: 44100,
            frequencyInterleaveIntervalSymbols: 1,
            randomSeed: 17);
        var ofdm = new OfdmGenerator(config);

        var resolveMethod = typeof(OfdmGenerator).GetMethod(
            "ResolveDataCarrierOrder",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolveMethod);

        var symbolLength = ofdm.SamplesPerOfdmSymbol;
        var order0 = (int[]?)resolveMethod!.Invoke(ofdm, [false, 0L, 0]);
        var order1 = (int[]?)resolveMethod.Invoke(ofdm, [false, (long)symbolLength, 0]);
        Assert.NotNull(order0);
        Assert.NotNull(order1);

        Assert.False(order0!.SequenceEqual(order1!));
    }

    [Fact]
    public void FrequencyInterleavePermutation_DiffersByInitSeed()
    {
        var config = new OfdmConfig(
            fftSize: 64,
            activeSubcarriers: 16,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: true,
            pilotSpacing: 8,
            sampleRate: 44100,
            frequencyInterleaveIntervalSymbols: 1,
            randomSeed: 17);
        var ofdm = new OfdmGenerator(config);

        var resolveMethod = typeof(OfdmGenerator).GetMethod(
            "ResolveDataCarrierOrder",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(resolveMethod);

        var fhOrder = (int[]?)resolveMethod!.Invoke(ofdm, [false, 0L, unchecked((int)0x13579BDF)]);
        var blockOrder = (int[]?)resolveMethod.Invoke(ofdm, [false, 0L, unchecked((int)0x2468ACE1)]);
        Assert.NotNull(fhOrder);
        Assert.NotNull(blockOrder);

        Assert.False(fhOrder!.SequenceEqual(blockOrder!));
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.IsType<T>(value);
        return (T)value!;
    }
}
