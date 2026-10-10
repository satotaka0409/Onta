using System;
using System.Numerics;
using Onta.Core;
using Onta.Core.Tests;

var input = @"C:\proj\Onta\Onta_core\test\in_files\Sample1.png";
var original = File.ReadAllBytes(input);
const double wow = 0.005;
const int seed = 20260911;
foreach (var sc in new[] { 16, 24 })
{
    var profile = new FileWavCodecProfile(sc, ModulationScheme.Qpsk, ChannelMode: ChannelMode.Stereo);
    var codec = new FileWavCodec(profile);
    var (left, right) = codec.EncodeFileToSamples(original, new FileInfo(input));
    var (wL, wR, trueWow, trueFlutter) = NoisePlus.ApplyWowFlutterInMemory(left, right, profile.SampleRate, wow, seed);
    var groupB = OfdmConfig.ResolveGroupBLeftBins();
    var grid = OfdmConfig.ResolveCarrierGrid(sc);
    var ofdm = new OfdmGenerator(new OfdmConfig(
        fftSize: 256,
        activeSubcarriers: groupB.Length,
        cyclicPrefixLength: profile.HeaderCyclicPrefixLength,
        ofdmSymbolCount: 1,
        modulationScheme: ModulationScheme.Bpsk,
        channelMode: ChannelMode.Mono,
        enableFrequencyInterleaving: true,
        pilotSpacing: 8,
        stereoFrequencyShiftBins: profile.StereoFrequencyShiftBins,
        sampleRate: profile.SampleRate,
        frequencyInterleaveIntervalSymbols: 1,
        randomSeed: profile.RandomSeed,
        conceptualLeftBins: groupB,
        carrierGrid: grid));
    var preambleStart = profile.LeadingSilenceSamples;
    var preambleCount = profile.UnmodulatedPreambleSamples;
    var trueScore = ofdm.ScoreWowParamsForDiagnostics(wL, false, preambleStart, preambleCount, wow, trueWow, trueFlutter);
    var matched = ofdm.MatchWowParametersForDiagnostics(wL, false, preambleStart, preambleCount);
    Console.WriteLine($"SC={sc} grid={grid} truePhase=({trueWow:F4},{trueFlutter:F4}) trueScore={trueScore:F6}");
    if (matched is { } m)
        Console.WriteLine($"  match baseline={m.Baseline:F6} best={m.BestScore:F6} amt={m.Amount:F5} ph=({m.WowPhase:F4},{m.FlutterPhase:F4}) ratio={m.BestScore/Math.Max(1e-12,m.Baseline):F3}");
    else
        Console.WriteLine("  match=null");
}
