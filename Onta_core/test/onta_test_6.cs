using System.Numerics;
using System.Reflection;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// サブキャリアグループごとのパイロット等化が閉域で適用されることを検証します。
/// </summary>
public sealed class OntaTest6
{
    [Fact]
    public void Decode_UsesBlockHeaderModulationMode_InsteadOfReceiverProfile()
    {
        var txProfile = new FileWavCodecProfile(
            ActiveSubcarriers: 36,
            ModulationScheme: ModulationScheme.Qam16,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);
        var rxProfile = txProfile with { ModulationScheme = ModulationScheme.Qam64 };

        var txCodec = new FileWavCodec(txProfile);
        var rxCodec = new FileWavCodec(rxProfile);
        var inputInfo = new FileInfo(TestPaths.ResolveInputPng());
        var payload = new byte[1024];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((i * 37) & 0xFF);
        }

        var (left, right) = txCodec.EncodeFileToSamples(payload, inputInfo);
        var decoded = rxCodec.DecodePcmSamplesToFileBytes(left, right, correctWow: false);

        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void HeaderOfdm_IsMonoEvenWhenProfileIsStereo()
    {
        var codec = new FileWavCodec(new FileWavCodecProfile(
            ActiveSubcarriers: 36,
            ModulationScheme: ModulationScheme.Qam64,
            ChannelMode: ChannelMode.Stereo));

        var method = typeof(FileWavCodec).GetMethod(
            "CreateHeaderOfdm",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var headerOfdm = (OfdmGenerator?)method!.Invoke(codec, [36]);
        Assert.NotNull(headerOfdm);
        Assert.Equal(ChannelMode.Mono, headerOfdm!.ChannelMode);
    }

    [Fact]
    public void StereoProfile_HeaderPreambleWaveform_IsIdenticalOnLeftAndRight()
    {
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 36,
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
    public void GroupD_Downgrade_AdjustsBitsPerOfdmSymbol_For36Sc64Qam()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(36, ChannelMode.Mono),
            activeSubcarriers: 36,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qam64,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: false,
            pilotSpacing: 9,
            randomSeed: 7,
            carrierGrid: OfdmCarrierGrid.Sc27Family);

        var ofdm = new OfdmGenerator(config);

        // 36 SC は 4 pilot + 32 data。A/B/C の 24 data は 64QAM(6bit)、D の 8 data は 16QAM(4bit)。
        Assert.Equal(176, ofdm.BitsPerOfdmSymbol);
    }

    [Fact]
    public void SampleCountForBitCount_UsesDowngradedGroupDCapacity()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(36, ChannelMode.Mono),
            activeSubcarriers: 36,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qam64,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: false,
            pilotSpacing: 9,
            randomSeed: 8,
            carrierGrid: OfdmCarrierGrid.Sc27Family);

        var ofdm = new OfdmGenerator(config);

        // 353bit は 176bit/symbol なら 3 symbol 必要（旧 192bit/symbol のままなら 2 symbol で誤る）。
        Assert.Equal(3 * ofdm.SamplesPerOfdmSymbol, ofdm.SampleCountForBitCount(353));
    }

    [Fact]
    public void PilotEqualizer_IsClosedWithinEachSubcarrierGroup()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(18, ChannelMode.Mono),
            activeSubcarriers: 18,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: false,
            pilotSpacing: 9,
            randomSeed: 1,
            carrierGrid: OfdmCarrierGrid.Sc9Family);
        var ofdm = new OfdmGenerator(config);

        var allCarriers = GetPrivateField<List<int>>(ofdm, "_leftAllCarrierBins");
        var pilotBins = GetPrivateField<List<int>>(ofdm, "_leftPilotBins").OrderBy(x => x).ToList();
        Assert.Equal(2, pilotBins.Count);

        var freqBins = new Complex[config.FftSize];
        freqBins[pilotBins[0]] = new Complex(0.5, 0.0);
        freqBins[pilotBins[1]] = new Complex(2.0, 0.0);

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

        var expectedGroup0 = equalizers![pilotBins[0]];
        var expectedGroup1 = equalizers[pilotBins[1]];
        Assert.True((expectedGroup0 - expectedGroup1).Magnitude > 0.3);

        foreach (var carrier in allCarriers)
        {
            var group = (int)groupMethod!.Invoke(null, [carrier, pilotBins])!;
            var actual = equalizers[carrier];
            var expected = group == 0 ? expectedGroup0 : expectedGroup1;
            Assert.InRange((actual - expected).Magnitude, 0.0, 1e-9);
        }
    }

    [Fact]
    public void PilotEqualizer_IsClosedWithinEachSubcarrierGroup_ForStereoLeftAndRight()
    {
        var config = new OfdmConfig(
            fftSize: OfdmConfig.ResolveFftSize(18, ChannelMode.Stereo),
            activeSubcarriers: 18,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Stereo,
            enableFrequencyInterleaving: false,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: 1,
            randomSeed: 2,
            carrierGrid: OfdmCarrierGrid.Sc9Family);
        var ofdm = new OfdmGenerator(config);

        var leftCarriers = GetPrivateField<List<int>>(ofdm, "_leftAllCarrierBins");
        var rightCarriers = GetPrivateField<List<int>>(ofdm, "_rightAllCarrierBins");
        var leftPilots = GetPrivateField<List<int>>(ofdm, "_leftPilotBins").OrderBy(x => x).ToList();
        var rightPilots = GetPrivateField<List<int>>(ofdm, "_rightPilotBins").OrderBy(x => x).ToList();
        Assert.Equal(2, leftPilots.Count);
        Assert.Equal(2, rightPilots.Count);

        var estimateMethod = typeof(OfdmGenerator).GetMethod(
            "EstimatePilotEqualizers",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(estimateMethod);

        var groupMethod = typeof(OfdmGenerator).GetMethod(
            "ResolvePilotGroupIndex",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(groupMethod);

        var leftFreqBins = new Complex[config.FftSize];
        leftFreqBins[leftPilots[0]] = new Complex(0.6, 0.0);
        leftFreqBins[leftPilots[1]] = new Complex(1.8, 0.0);
        var leftEqualizers = (Complex[]?)estimateMethod!.Invoke(ofdm, [leftFreqBins, leftPilots, false, null]);
        Assert.NotNull(leftEqualizers);

        var leftExpectedGroup0 = leftEqualizers![leftPilots[0]];
        var leftExpectedGroup1 = leftEqualizers[leftPilots[1]];
        Assert.True((leftExpectedGroup0 - leftExpectedGroup1).Magnitude > 0.2);
        foreach (var carrier in leftCarriers)
        {
            var group = (int)groupMethod!.Invoke(null, [carrier, leftPilots])!;
            var expected = group == 0 ? leftExpectedGroup0 : leftExpectedGroup1;
            Assert.InRange((leftEqualizers[carrier] - expected).Magnitude, 0.0, 1e-9);
        }

        var rightFreqBins = new Complex[config.FftSize];
        rightFreqBins[rightPilots[0]] = new Complex(1.4, 0.0);
        rightFreqBins[rightPilots[1]] = new Complex(0.4, 0.0);
        var rightEqualizers = (Complex[]?)estimateMethod.Invoke(ofdm, [rightFreqBins, rightPilots, true, null]);
        Assert.NotNull(rightEqualizers);

        var rightExpectedGroup0 = rightEqualizers![rightPilots[0]];
        var rightExpectedGroup1 = rightEqualizers[rightPilots[1]];
        Assert.True((rightExpectedGroup0 - rightExpectedGroup1).Magnitude > 0.2);
        foreach (var carrier in rightCarriers)
        {
            var group = (int)groupMethod.Invoke(null, [carrier, rightPilots])!;
            var expected = group == 0 ? rightExpectedGroup0 : rightExpectedGroup1;
            Assert.InRange((rightEqualizers[carrier] - expected).Magnitude, 0.0, 1e-9);
        }

        Assert.True((leftEqualizers[leftPilots[0]] - rightEqualizers[rightPilots[0]]).Magnitude > 0.01);
    }

    [Fact]
    public void FrequencyInterleavePermutation_ChangesEveryOfdmSymbol()
    {
        var config = new OfdmConfig(
            fftSize: 64,
            activeSubcarriers: 18,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
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
            activeSubcarriers: 18,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: true,
            pilotSpacing: 9,
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