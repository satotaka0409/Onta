using System.Numerics;
using Onta.Core;
using Onta.Core.Tests;

namespace Onta.Core.TestHost;

/// <summary>
/// デバッグ実行用テストホストです。引数で対象テストを切り替えます。
/// probe* のうち exact 位相を渡すものは診断用オラクルであり、本テスト（OntaTest*）の代替ではありません。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var target = args.Length > 0 ? args[0] : "1";
        Console.WriteLine($"Onta TestHost: running test {target} under debugger...");

        try
        {
            switch (target)
            {
                case "1":
                    new OntaTest1().EncodeDecode_QrPng_MatchesOriginal_Stereo9ScQpsk();
                    break;
                case "2":
                    new OntaTest2().EncodeDecode_QrPng_MatchesOriginal_Stereo36Sc64Qam();
                    break;
                case "3":
                    new OntaTest3().EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WithNoiseAndWowFlutter();
                    break;
                case "3w":
                    new OntaTest3().EncodeDecode_QrPng_MatchesOriginal_Stereo18Sc16Qam_WowOnly();
                    break;
                case "4":
                    new OntaTest4().EncodeDecode_QrPng_MatchesOriginal_Mono27ScQpsk_WithNoiseAndWowFlutter();
                    break;
                case "probe3m":
                    ProbeWowMatchStereo(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test3_wow_only.wav", impairmentSeed: 20260904);
                    break;
                case "probe3c":
                    ProbeExactCorrectCorr(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test3_wow_only.wav", 20260904, ChannelMode.Stereo);
                    break;
                case "probe3p":
                    ProbeWithWowParamsTest3();
                    break;
                case "probe3w":
                    ProbeUiStyleDecode(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test3_wow_only.wav", correctWow: true);
                    break;
                case "probe3r":
                    ProbeMatchRefineDecodeTest3();
                    break;
                case "probe4":
                    ProbeUiStyleDecode(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test4.wav", correctWow: false);
                    break;
                case "probe4w":
                    ProbeUiStyleDecode(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test4.wav", correctWow: true);
                    break;
                case "probe4p":
                    ProbeWithWowParams(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test4.wav");
                    break;
                case "probe4m":
                    ProbeWowMatch(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test4.wav");
                    break;
                case "probe4c":
                    ProbeMatchThenCorrect(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test4.wav");
                    break;
                case "probe4r":
                    ProbeResampleExact(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test4.wav");
                    break;
                case "probe4s":
                    ProbeCorrectScores(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test4.wav");
                    break;
                case "9":
                    new OntaTest9().Decode_MatchesOriginal_Stereo27ScQpsk_WithWowLpfAndNoise();
                    break;
                case "probe9":
                    ProbeUiStyleDecode(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test9_rx_st27_qpsk_lpf.wav", correctWow: false);
                    break;
                case "probe9w":
                    ProbeUiStyleDecode(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test9_rx_st27_qpsk_lpf.wav", correctWow: true);
                    break;
                case "probe9p":
                    ProbeTest9ExactWow(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test9_rx_st27_qpsk_lpf.wav");
                    break;
                case "probe9m":
                    ProbeTest9Match(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test9_rx_st27_qpsk_lpf.wav");
                    break;
                case "probe9seg":
                    ProbeTest9SegmentCorrect(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test9_rx_st27_qpsk_lpf.wav");
                    break;
                case "probe9bh":
                    ProbeTest9BhWowWindow(@"C:\proj\Onta\Onta_core\test\out_files\Sample1_test9_rx_st27_qpsk_lpf.wav");
                    break;
                default:
                    Console.Error.WriteLine("Usage: Onta_core.TestHost [...|probe9bh]");
                    return 2;
            }

            Console.WriteLine("PASS");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }


    private static void ProbeWithWowParamsTest3()
    {
        var (wowPhase, flutterPhase) = WowFlutterWarp.CreatePhases(20260904);
        var wavPath = @"C:\proj\Onta\Onta_core\test\out_files\Sample1_test3_wow_only.wav";
        Console.WriteLine($"Probe exact wowParams amount=0.01 phases=({wowPhase:F4},{flutterPhase:F4})");
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 18,
            ModulationScheme: ModulationScheme.Qam16,
            ChannelMode: ChannelMode.Stereo);
        var codec = new FileWavCodec(profile);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decoded = codec.DecodeWavToFileBytes(
            wavPath,
            correctWow: false,
            wowParams: (0.01, wowPhase, flutterPhase));
        Console.WriteLine($"OK bytes={decoded.Length} elapsed={sw.Elapsed}");
    }

    private static void ProbeMatchRefineDecodeTest3()
    {
        var wavPath = @"C:\proj\Onta\Onta_core\test\out_files\Sample1_test3_wow_only.wav";
        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260904);
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 18,
            ModulationScheme: ModulationScheme.Qam16,
            ChannelMode: ChannelMode.Stereo);
        var (left, _) = WavReader.ReadPcm16(wavPath);
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
        var match = ofdm.MatchWowParametersForDiagnostics(left, false, start, count)
            ?? throw new InvalidOperationException("Match null");
        Console.WriteLine($"true  wow={trueWow:F6} flutter={trueFlutter:F6}");
        Console.WriteLine($"match wow={match.WowPhase:F6} flutter={match.FlutterPhase:F6} best={match.BestScore:F4}");
        Console.WriteLine($"score(true)={ofdm.ScoreWowParamsForDiagnostics(left, false, start, count, 0.01, trueWow, trueFlutter):F6}");
        Console.WriteLine($"score(match)={ofdm.ScoreWowParamsForDiagnostics(left, false, start, count, match.Amount, match.WowPhase, match.FlutterPhase):F6}");

        var refined = ofdm.RefineWowParametersForCorrectModel(
            left, false, start, count, match.Amount, match.WowPhase, match.FlutterPhase);
        Console.WriteLine($"refine amt={refined.Amount:F4} wow={refined.WowPhase:F6} flutter={refined.FlutterPhase:F6}");
        Console.WriteLine($"score(refine)={ofdm.ScoreWowParamsForDiagnostics(left, false, start, count, refined.Amount, refined.WowPhase, refined.FlutterPhase):F6}");

        var codec = new FileWavCodec(profile);
        void TryDecode(string label, (double, double, double) wp)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bytes = codec.DecodeWavToFileBytes(wavPath, correctWow: false, wowParams: wp);
                Console.WriteLine($"{label} OK bytes={bytes.Length} elapsed={sw.Elapsed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label} FAIL {ex.Message}");
            }
        }

        TryDecode("true", (0.01, trueWow, trueFlutter));
        if (match.BestScore >= 0.95)
        {
            TryDecode("match", (match.Amount, match.WowPhase, match.FlutterPhase));
        }
        else
        {
            Console.WriteLine($"match skip decode (best={match.BestScore:F4})");
        }

        var refineScore = ofdm.ScoreWowParamsForDiagnostics(
            left, false, start, count, refined.Amount, refined.WowPhase, refined.FlutterPhase);
        if (refineScore >= 0.95)
        {
            TryDecode("refine", refined);
        }
        else
        {
            Console.WriteLine($"refine skip decode (score={refineScore:F4})");
        }
    }

    private static void ProbeWithWowParams(string wavPath)
    {
        // test4 と同じ ImpairmentSeed=20260905 から位相を再現する。
        var (wowPhase, flutterPhase) = WowFlutterWarp.CreatePhases(20260905);
        Console.WriteLine($"Probe with exact wowParams amount=0.01 phases=({wowPhase:F4},{flutterPhase:F4}): {wavPath}");
        var channels = WavReader.PeekChannelCount(wavPath);
        var channelMode = channels == 2 ? ChannelMode.Stereo : ChannelMode.Mono;
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: channelMode,
            BlockInterleaveFactor: 1);
        var codec = new FileWavCodec(profile);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decoded = codec.DecodeWavToFileBytes(
            wavPath,
            correctWow: false,
            wowParams: (0.01, wowPhase, flutterPhase));
        Console.WriteLine($"OK bytes={decoded.Length} elapsed={sw.Elapsed}");
    }

    /// <summary>
    /// UI 受信条件で既存 WAV を復号します。
    /// </summary>
    private static void ProbeUiStyleDecode(string wavPath, bool correctWow)
    {
        Console.WriteLine($"Probe decode: {wavPath} correctWow={correctWow}");
        if (!File.Exists(wavPath))
        {
            throw new FileNotFoundException(wavPath);
        }

        var channels = WavReader.PeekChannelCount(wavPath);
        var channelMode = channels == 2 ? ChannelMode.Stereo : ChannelMode.Mono;
        // test3 wow_only は 18SC/16QAM。それ以外はヘッダーのみで復調できる最低設定。
        var isTest3Wow = wavPath.Contains("test3_wow_only", StringComparison.OrdinalIgnoreCase)
            || wavPath.Contains("Sample1_test3", StringComparison.OrdinalIgnoreCase);
        var profile = isTest3Wow
            ? new FileWavCodecProfile(
                ActiveSubcarriers: 18,
                ModulationScheme: ModulationScheme.Qam16,
                ChannelMode: channelMode,
                BlockInterleaveFactor: 1)
            : new FileWavCodecProfile(
                ActiveSubcarriers: 9,
                ModulationScheme: ModulationScheme.Bpsk,
                ChannelMode: channelMode,
                BlockInterleaveFactor: 1);
        var codec = new FileWavCodec(profile);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine($"channels={channels} size={new FileInfo(wavPath).Length:N0} bytes");
        try
        {
            var decoded = codec.DecodeWavToFileBytes(wavPath, correctWow: correctWow, wowParams: null);
            Console.WriteLine($"OK bytes={decoded.Length} elapsed={sw.Elapsed}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL elapsed={sw.Elapsed} err={ex.Message}");
            throw;
        }
    }

    private static void ProbeExactCorrectCorr(string wavPath, int seed, ChannelMode channelMode)
    {
        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(seed);
        Console.WriteLine($"Exact Correct corr probe seed={seed} phases=({trueWow:F4},{trueFlutter:F4})");
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: channelMode == ChannelMode.Stereo ? 18 : 27,
            ModulationScheme: ModulationScheme.Qam16,
            ChannelMode: channelMode);
        var (left, _) = WavReader.ReadPcm16(wavPath);
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        var grid = OfdmConfig.ResolveCarrierGrid(profile.ActiveSubcarriers);
        var fftSize = OfdmConfig.ResolveFftSize(9, channelMode);
        var ofdm = new OfdmGenerator(new OfdmConfig(
            fftSize: fftSize,
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
            carrierGrid: grid));

        var start = profile.LeadingSilenceSamples;
        var count = profile.UnmodulatedPreambleSamples;
        var ideal = ofdm.GenerateUnmodulated(count).Left;
        var rawSlice = left.AsSpan(start, count).ToArray();
        var corrRaw = Correlate(rawSlice, ideal);
        Console.WriteLine($"raw corr={corrRaw:F4}");

        var work = (Complex[])left.Clone();
        WowFlutterWarp.CorrectInPlace(work, profile.SampleRate, 0.01, trueWow, trueFlutter);
        var corrFull = Correlate(work.AsSpan(start, count).ToArray(), ideal);
        Console.WriteLine($"full-Correct corr={corrFull:F4}");

        var prefixLen = start + count;
        var prefix = new Complex[prefixLen];
        Array.Copy(left, prefix, prefixLen);
        WowFlutterWarp.CorrectInPlace(prefix, prefixLen, profile.SampleRate, 0.01, trueWow, trueFlutter);
        var corrPrefix = Correlate(prefix.AsSpan(start, count).ToArray(), ideal);
        Console.WriteLine($"prefix-Correct corr={corrPrefix:F4}");

        var leftForSeg = (Complex[])left.Clone();
        ofdm.CorrectWowFlutterSegmentInPlace(leftForSeg, start, count, 0.01, trueWow, trueFlutter);
        var corrSeg = Correlate(leftForSeg.AsSpan(start, count).ToArray(), ideal);
        Console.WriteLine($"segment-Correct corr={corrSeg:F4}");

        var prefixScore = new Complex[count];
        WowFlutterWarp.CorrectPrefixWithReferenceLength(
            left,
            left.Length,
            start,
            count,
            profile.SampleRate,
            0.01,
            trueWow,
            trueFlutter,
            prefixScore);
        Console.WriteLine($"prefixRef-Correct corr={Correlate(prefixScore, ideal):F4}");

        // Match 近傍から Correct 精密化が真値へ戻るか
        var matchLikeWow = trueWow + 0.01;
        var matchLikeFlutter = trueFlutter + 0.008;
        var refined = ofdm.RefineWowParametersForCorrectModel(
            left,
            useRightChannel: false,
            start,
            count,
            0.01,
            matchLikeWow,
            matchLikeFlutter);
        Console.WriteLine(
            $"refine from offset -> amount={refined.Amount:F4} wow={refined.WowPhase:F4} flutter={refined.FlutterPhase:F4} " +
            $"dWow={Wrap(refined.WowPhase - trueWow):F5} dFlutter={Wrap(refined.FlutterPhase - trueFlutter):F5}");

        void PrintPrefix(string label, double a, double w, double f)
        {
            var buf = new Complex[count];
            WowFlutterWarp.CorrectPrefixWithReferenceLength(
                left, left.Length, start, count, profile.SampleRate, a, w, f, buf);
            var work2 = (Complex[])left.Clone();
            WowFlutterWarp.CorrectInPlace(work2, profile.SampleRate, a, w, f);
            Console.WriteLine(
                $"{label}: prefixRef={Correlate(buf, ideal):F4} full={Correlate(work2.AsSpan(start, count).ToArray(), ideal):F4}");
        }

        PrintPrefix("at true", 0.01, trueWow, trueFlutter);
        PrintPrefix("at refined", refined.Amount, refined.WowPhase, refined.FlutterPhase);
        PrintPrefix("at startOffset", 0.01, matchLikeWow, matchLikeFlutter);

        Console.WriteLine("fine flutter scan around offset:");
        for (var dF = -15; dF <= 15; dF += 5)
        {
            var f = matchLikeFlutter + (dF * Math.PI / 4000.0);
            var buf = new Complex[count];
            WowFlutterWarp.CorrectPrefixWithReferenceLength(
                left, left.Length, start, count, profile.SampleRate, 0.01, matchLikeWow, f, buf);
            Console.WriteLine($"  dF={dF,3} f={f:F4} prefixRef={Correlate(buf, ideal):F4}");
        }
    }

    private static double Wrap(double phase)
    {
        var twoPi = 2.0 * Math.PI;
        phase %= twoPi;
        if (phase > Math.PI)
        {
            phase -= twoPi;
        }
        else if (phase < -Math.PI)
        {
            phase += twoPi;
        }

        return phase;
    }

    private static double Correlate(Complex[] a, Complex[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var meanA = 0.0;
        var meanB = 0.0;
        for (var i = 0; i < n; i++)
        {
            meanA += a[i].Real;
            meanB += b[i].Real;
        }

        meanA /= n;
        meanB /= n;
        var num = 0.0;
        var denA = 0.0;
        var denB = 0.0;
        for (var i = 0; i < n; i++)
        {
            var da = a[i].Real - meanA;
            var db = b[i].Real - meanB;
            num += da * db;
            denA += da * da;
            denB += db * db;
        }

        var den = Math.Sqrt(denA * denB);
        return den > 1e-18 ? num / den : 0.0;
    }

    private static void ProbeWowMatch(string wavPath)
    {
        ProbeWowMatchStereo(wavPath, impairmentSeed: 20260905, forceMono: true);
    }

    private static void ProbeWowMatchStereo(string wavPath, int impairmentSeed, bool forceMono = false)
    {
        var (wowPhase, flutterPhase) = WowFlutterWarp.CreatePhases(impairmentSeed);
        Console.WriteLine($"True phases wow={wowPhase:F4} flutter={flutterPhase:F4} seed={impairmentSeed}");

        var channels = forceMono ? 1 : WavReader.PeekChannelCount(wavPath);
        var channelMode = channels == 2 ? ChannelMode.Stereo : ChannelMode.Mono;
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: channelMode,
            BlockInterleaveFactor: 1);
        var (left, _) = WavReader.ReadPcm16(wavPath);
        Console.WriteLine($"samples={left.Length} chMode={channelMode}");
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        var grid = OfdmConfig.ResolveCarrierGrid(9);
        var fftSize = OfdmConfig.ResolveFftSize(9, channelMode);
        var ofdm = new OfdmGenerator(new OfdmConfig(
            fftSize: fftSize,
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
            carrierGrid: grid));

        void Report(string label, int start, int count)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var diag = ofdm.MatchWowParametersForDiagnostics(left, useRightChannel: false, start, count);
            if (diag is null)
            {
                Console.WriteLine($"{label}: Match=null start={start} count={count} elapsed={sw.Elapsed}");
                return;
            }

            Console.WriteLine(
                $"{label}: baseline={diag.Value.Baseline:F4} best={diag.Value.BestScore:F4} " +
                $"gain={diag.Value.BestScore - diag.Value.Baseline:F4} " +
                $"amt={diag.Value.Amount:F4} wow={diag.Value.WowPhase:F4} flutter={diag.Value.FlutterPhase:F4} " +
                $"elapsed={sw.Elapsed}");
        }

        var sr = profile.SampleRate;
        Report("preamble", profile.LeadingSilenceSamples, profile.UnmodulatedPreambleSamples);
        var fhStart = profile.LeadingSilenceSamples + profile.UnmodulatedPreambleSamples;
        Report("fh-unmod", fhStart, profile.FileHeaderUnmodulatedSamples);
        Report("around-fh", Math.Max(0, fhStart - (sr / 8)), sr / 2);
    }

    private static void ProbeMatchThenCorrect(string wavPath)
    {
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: ChannelMode.Mono,
            BlockInterleaveFactor: 1);
        var (left, right) = WavReader.ReadPcm16(wavPath);
        var groupB = OfdmConfig.ResolveGroupBLeftBins();
        var grid = OfdmConfig.ResolveCarrierGrid(9);
        var fftSize = OfdmConfig.ResolveFftSize(9, ChannelMode.Mono);
        var ofdm = new OfdmGenerator(new OfdmConfig(
            fftSize: fftSize,
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
            carrierGrid: grid));

        var diag = ofdm.MatchWowParametersForDiagnostics(
            left,
            useRightChannel: false,
            profile.LeadingSilenceSamples,
            profile.UnmodulatedPreambleSamples);
        if (diag is null)
        {
            Console.WriteLine("Match failed");
            return;
        }

        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260905);
        Console.WriteLine(
            $"matched amt={diag.Value.Amount:F4} wow={diag.Value.WowPhase:F4} flutter={diag.Value.FlutterPhase:F4} " +
            $"true wow={trueWow:F4} flutter={trueFlutter:F4}");

        var refined = ofdm.RefineWowParametersForCorrectModel(
            left,
            useRightChannel: false,
            profile.LeadingSilenceSamples,
            profile.UnmodulatedPreambleSamples,
            diag.Value.Amount,
            diag.Value.WowPhase,
            diag.Value.FlutterPhase);
        Console.WriteLine($"refined amt={refined.Amount:F4} wow={refined.WowPhase:F6} flutter={refined.FlutterPhase:F6}");
        Console.WriteLine($"true     amt=0.0100 wow={trueWow:F6} flutter={trueFlutter:F6}");

        var codec = new FileWavCodec(profile);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decoded = codec.DecodeWavToFileBytes(wavPath, correctWow: false, wowParams: refined);
        Console.WriteLine($"OK bytes={decoded.Length} elapsed={sw.Elapsed}");
    }

    private static void ProbeResampleExact(string wavPath)
    {
        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260905);
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: ChannelMode.Mono,
            BlockInterleaveFactor: 1);
        var (left, _) = WavReader.ReadPcm16(wavPath);
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

        Console.WriteLine($"Resample-correct full buffer with exact phases wow={trueWow:F4} flutter={trueFlutter:F4}");
        ofdm.CorrectWowFlutterSegmentInPlace(left, 0, left.Length, 0.01, trueWow, trueFlutter);

        var codec = new FileWavCodec(profile);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decoded = codec.DecodePcmSamplesToFileBytes(left, Array.Empty<System.Numerics.Complex>(), correctWow: false, wowParams: null);
        Console.WriteLine($"OK bytes={decoded.Length} elapsed={sw.Elapsed}");
    }

    private static void ProbeCorrectScores(string wavPath)
    {
        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260905);
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: ChannelMode.Mono,
            BlockInterleaveFactor: 1);
        var (left, _) = WavReader.ReadPcm16(wavPath);
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

        var diag = ofdm.MatchWowParametersForDiagnostics(
            left, false, profile.LeadingSilenceSamples, profile.UnmodulatedPreambleSamples)!;
        var start = profile.LeadingSilenceSamples;
        var count = profile.UnmodulatedPreambleSamples;
        var ideal = ofdm.GenerateUnmodulated(count).Left;
        var sr = profile.SampleRate;

        double Score(double wow, double flutter)
        {
            var swScore = System.Diagnostics.Stopwatch.StartNew();
            var corrected = WowFlutterWarp.Correct(left, sr, 0.01, wow, flutter);
            Console.WriteLine($"  Correct(full) took {swScore.Elapsed}");
            var meanA = 0.0;
            var meanB = 0.0;
            for (var i = 0; i < count; i++)
            {
                meanA += corrected[start + i].Real;
                meanB += ideal[i].Real;
            }

            meanA /= count;
            meanB /= count;
            var num = 0.0;
            var denA = 0.0;
            var denB = 0.0;
            for (var i = 0; i < count; i++)
            {
                var da = corrected[start + i].Real - meanA;
                var db = ideal[i].Real - meanB;
                num += da * db;
                denA += da * da;
                denB += db * db;
            }

            return num / Math.Sqrt(denA * denB);
        }

        var wrappedTrueFlutter = trueFlutter;
        while (wrappedTrueFlutter > Math.PI) wrappedTrueFlutter -= 2 * Math.PI;
        while (wrappedTrueFlutter < -Math.PI) wrappedTrueFlutter += 2 * Math.PI;

        Console.WriteLine($"score match: {Score(diag.Value.WowPhase, diag.Value.FlutterPhase):F6}");
        Console.WriteLine($"score true:  {Score(trueWow, trueFlutter):F6}");
        Console.WriteLine($"score trueW: {Score(trueWow, wrappedTrueFlutter):F6}");
    }

    private static void ProbeTest9ExactWow(string wavPath)
    {
        // test9: amount=0.005, seed=20260911
        var (wowPhase, flutterPhase) = WowFlutterWarp.CreatePhases(20260911);
        Console.WriteLine($"Probe test9 exact wow amount=0.005 phases=({wowPhase:F4},{flutterPhase:F4})");
        Console.WriteLine($"  UI profile (SC9 placeholder) + exact wowParams");
        var uiProfile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);
        var testProfile = new FileWavCodecProfile(
            ActiveSubcarriers: 27,
            ModulationScheme: ModulationScheme.Qpsk,
            ChannelMode: ChannelMode.Stereo);

        void Try(string label, FileWavCodecProfile profile, bool correctWow, (double, double, double)? wow)
        {
            var codec = new FileWavCodec(profile);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var decoded = codec.DecodeWavToFileBytes(wavPath, correctWow, wow);
                Console.WriteLine($"{label}: OK bytes={decoded.Length} elapsed={sw.Elapsed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: FAIL {ex.Message} elapsed={sw.Elapsed}");
            }
        }

        Try("testProfile+exact", testProfile, correctWow: false, (0.005, wowPhase, flutterPhase));
        Try("uiProfile+exact", uiProfile, correctWow: false, (0.005, wowPhase, flutterPhase));
        Try("uiProfile+noWow", uiProfile, correctWow: false, null);
    }

    private static void ProbeTest9Match(string wavPath)
    {
        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260911);
        Console.WriteLine($"true amount=0.005 wow={trueWow:F4} flutter={trueFlutter:F4}");

        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);
        var (left, _) = WavReader.ReadPcm16(wavPath);
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var diag = ofdm.MatchWowParametersForDiagnostics(
            left, false, profile.LeadingSilenceSamples, profile.UnmodulatedPreambleSamples);
        Console.WriteLine($"Match elapsed={sw.Elapsed}");
        if (diag is null)
        {
            Console.WriteLine("Match=null");
            return;
        }

        Console.WriteLine(
            $"matched amt={diag.Value.Amount:F4} wow={diag.Value.WowPhase:F4} flutter={diag.Value.FlutterPhase:F4} " +
            $"score={diag.Value.BestScore:F4} gain={diag.Value.BestScore - diag.Value.Baseline:F4}");

        var refined = ofdm.RefineWowParametersForCorrectModel(
            left, false, profile.LeadingSilenceSamples, profile.UnmodulatedPreambleSamples,
            diag.Value.Amount, diag.Value.WowPhase, diag.Value.FlutterPhase);
        Console.WriteLine($"refined amt={refined.Amount:F4} wow={refined.WowPhase:F4} flutter={refined.FlutterPhase:F4}");

        var codec = new FileWavCodec(profile);
        void Try(string label, (double, double, double) wp)
        {
            var t = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var decoded = codec.DecodeWavToFileBytes(wavPath, correctWow: false, wowParams: wp);
                Console.WriteLine($"{label}: OK bytes={decoded.Length} elapsed={t.Elapsed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: FAIL {ex.Message} elapsed={t.Elapsed}");
            }
        }

        Try("matched", (diag.Value.Amount, diag.Value.WowPhase, diag.Value.FlutterPhase));
        Try("refined", refined);
        Try("exact", (0.005, trueWow, trueFlutter));
    }

    private static void ProbeTest9BhWowWindow(string wavPath)
    {
        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260911);
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);
        var codec = new FileWavCodec(profile);
        Console.WriteLine($"true wow={trueWow:F4} flutter={trueFlutter:F4}");

        void Try(string label, double wow, double flutter)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var decoded = codec.DecodeWavToFileBytes(
                    wavPath, correctWow: false, wowParams: (0.005, wow, flutter));
                Console.WriteLine($"{label}: OK bytes={decoded.Length} elapsed={sw.Elapsed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{label}: FAIL {ex.Message} elapsed={sw.Elapsed}");
            }
        }

        Try("exact", trueWow, trueFlutter);
        foreach (var d in new[] { 0.05, 0.15, 0.35 })
        {
            Try($"wow+{d:F2}", trueWow + d, trueFlutter);
            Try($"fl+{d:F2}", trueWow, trueFlutter + d);
        }
    }

    /// <summary>
    /// Exact 位相を CorrectWowFlutterSegmentInPlace（絶対時刻モデル）で掛けてから復号します。
    /// </summary>
    private static void ProbeTest9SegmentCorrect(string wavPath)
    {
        var (trueWow, trueFlutter) = WowFlutterWarp.CreatePhases(20260911);
        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: 9,
            ModulationScheme: ModulationScheme.Bpsk,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1);
        var (left, right) = WavReader.ReadPcm16(wavPath);
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

        Console.WriteLine($"SegmentCorrect exact amount=0.005 wow={trueWow:F4} flutter={trueFlutter:F4}");
        ofdm.CorrectWowFlutterSegmentInPlace(left, 0, left.Length, 0.005, trueWow, trueFlutter);
        ofdm.CorrectWowFlutterSegmentInPlace(right, 0, right.Length, 0.005, trueWow, trueFlutter);

        var codec = new FileWavCodec(profile);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var decoded = codec.DecodePcmSamplesToFileBytes(left, right, correctWow: false, wowParams: null);
        Console.WriteLine($"OK bytes={decoded.Length} elapsed={sw.Elapsed}");
    }
}

