using System;
using System.Diagnostics;
using System.Numerics;
using Onta.Core;

var wav = @"C:\proj\Onta\Onta_core\test\out_files\Sample1_test3_wow_only.wav";
var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260904);
var profile = new FileWavCodecProfile(ActiveSubcarriers: 18, ModulationScheme: ModulationScheme.Qam16, ChannelMode: ChannelMode.Stereo);
var (left, _) = WavReader.ReadPcm16(wav);
var groupB = OfdmConfig.ResolveGroupBLeftBins();
var ofdm = new OfdmGenerator(new OfdmConfig(
    fftSize: OfdmConfig.ResolveFftSize(9, ChannelMode.Mono),
    activeSubcarriers: groupB.Length,
    cyclicPrefixLength: profile.HeaderCyclicPrefixLength,
    ofdmSymbolCount: 1,
    modulationScheme: ModulationScheme.Bpsk,
    channelMode: ChannelMode.Mono,
    enableFrequencyInterleaving: true,
    pilotSpacing: 9,
    stereoFrequencyShiftBins: profile.StereoFrequencyShiftBins,
    sampleRate: profile.SampleRate,
    frequencyInterleaveIntervalSymbols: 1,
    randomSeed: profile.RandomSeed,
    conceptualLeftBins: groupB,
    carrierGrid: OfdmConfig.ResolveCarrierGrid(9)));

var start = profile.LeadingSilenceSamples;
var count = profile.UnmodulatedPreambleSamples;
var match = ofdm.MatchWowParametersForDiagnostics(left, false, start, count)!;
Console.WriteLine($"true  wow={trueWow:F6} flutter={trueFlutter:F6}");
Console.WriteLine($"match wow={match.Value.WowPhase:F6} flutter={match.Value.FlutterPhase:F6} score={match.Value.BestScore:F4}");

double Score(double a, double w, double f) =>
    ofdm.ScoreWowParamsForDiagnostics(left, false, start, count, a, w, f);

Console.WriteLine($"score(true)={Score(0.01, trueWow, trueFlutter):F6}");
Console.WriteLine($"score(match)={Score(0.01, match.Value.WowPhase, match.Value.FlutterPhase):F6}");

var refined = ofdm.RefineWowParametersForCorrectModel(left, false, start, count, match.Value.Amount, match.Value.WowPhase, match.Value.FlutterPhase);
Console.WriteLine($"refine amt={refined.Amount:F4} wow={refined.WowPhase:F6} flutter={refined.FlutterPhase:F6}");
Console.WriteLine($"score(refine)={Score(refined.Amount, refined.WowPhase, refined.FlutterPhase):F6}");

// decode with refine params
var codec = new FileWavCodec(profile);
try {
  var sw=Stopwatch.StartNew();
  var bytes = codec.DecodeWavToFileBytes(wav, correctWow:false, wowParams: refined);
  Console.WriteLine($"decode refine OK {bytes.Length} in {sw.Elapsed}");
} catch (Exception ex) { Console.WriteLine($"decode refine FAIL {ex.Message}"); }

try {
  var sw=Stopwatch.StartNew();
  var bytes = codec.DecodeWavToFileBytes(wav, correctWow:false, wowParams: (0.01, trueWow, trueFlutter));
  Console.WriteLine($"decode true OK {bytes.Length} in {sw.Elapsed}");
} catch (Exception ex) { Console.WriteLine($"decode true FAIL {ex.Message}"); }

try {
  var sw=Stopwatch.StartNew();
  var bytes = codec.DecodeWavToFileBytes(wav, correctWow:false, wowParams: (match.Value.Amount, match.Value.WowPhase, match.Value.FlutterPhase));
  Console.WriteLine($"decode match OK {bytes.Length} in {sw.Elapsed}");
} catch (Exception ex) { Console.WriteLine($"decode match FAIL {ex.Message}"); }
