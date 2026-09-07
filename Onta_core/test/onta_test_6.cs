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
    public void PilotEqualizer_IsClosedWithinEachSubcarrierGroup()
    {
        var config = new OfdmConfig(
            fftSize: 64,
            activeSubcarriers: 18,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Mono,
            enableFrequencyInterleaving: false,
            pilotSpacing: 9,
            randomSeed: 1);
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
            fftSize: 64,
            activeSubcarriers: 18,
            cyclicPrefixLength: 16,
            ofdmSymbolCount: 1,
            modulationScheme: ModulationScheme.Qpsk,
            channelMode: ChannelMode.Stereo,
            enableFrequencyInterleaving: false,
            pilotSpacing: 9,
            stereoFrequencyShiftBins: 1,
            randomSeed: 2);
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

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field!.GetValue(instance);
        Assert.IsType<T>(value);
        return (T)value!;
    }
}