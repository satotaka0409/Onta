using System.Diagnostics;
using System.Numerics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Onta.Core;

BenchmarkRunner.Run<FhBhSyncBenchmark>();

[MemoryDiagnoser]
public class FhBhSyncBenchmark
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 16,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Stereo);

    private Complex[] _leftRx = [];
    private Complex[] _rightRx = [];

    [Params(16384, 32768)]
    public int ChunkSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var inputPath = ResolveInputPngPath();
        var sourcePayload = File.ReadAllBytes(inputPath);
        var payload = sourcePayload.Length > 2 * 1024
            ? sourcePayload.AsSpan(0, 2 * 1024).ToArray()
            : sourcePayload;

        var workDir = Path.Combine(Path.GetTempPath(), "onta_bench");
        Directory.CreateDirectory(workDir);
        var benchInputPath = Path.Combine(workDir, "fh_bh_sync_benchmark_input.bin");
        var wavPath = Path.Combine(workDir, "fh_bh_sync_benchmark.wav");

        File.WriteAllBytes(benchInputPath, payload);

        var codecForEncode = new FileWavCodec(Profile);
        var (leftTx, rightTx) = codecForEncode.EncodeFileToSamples(payload, new FileInfo(benchInputPath));
        WavWriter.WriteStereo16(wavPath, Profile.SampleRate, leftTx, rightTx, Profile.SamplePeak);

        (_leftRx, _rightRx) = WavReader.ReadPcm16(wavPath);
    }

    [Benchmark]
    public double MeasureFhBhSyncMs_WithGuard()
    {
        var codec = new FileWavCodec(Profile);
        var state = new ProgressiveDecodeState();

        var fhSyncMs = -1.0;
        var bhSyncMs = -1.0;
        var cursor = 0;
        var sw = Stopwatch.StartNew();

        var analysisLimitSamples = Math.Min(_leftRx.Length, Profile.SampleRate * 8);
        var maxIterations = (analysisLimitSamples / Math.Max(ChunkSize, 1024)) + 4;
        var iteration = 0;

        while (cursor < analysisLimitSamples && iteration < maxIterations)
        {
            iteration++;
            cursor = Math.Min(analysisLimitSamples, cursor + ChunkSize);

            var leftChunk = new Complex[cursor];
            Array.Copy(_leftRx, 0, leftChunk, 0, cursor);

            var rightChunk = new Complex[cursor];
            if (_rightRx.Length > 0)
            {
                Array.Copy(_rightRx, 0, rightChunk, 0, Math.Min(cursor, _rightRx.Length));
            }

            var status = codec.DecodePcmSamplesProgressive(
                leftChunk,
                rightChunk,
                state,
                correctWow: false,
                wowParams: null,
                tuning: null,
                allowIncomplete: true);

            if (fhSyncMs < 0 && state.HeaderReady)
            {
                fhSyncMs = sw.Elapsed.TotalMilliseconds;
            }

            var exec = state.ReadExecutionStatus();
            if (bhSyncMs < 0
                && exec.Progress.CurrentBlockIndex >= 0
                && exec.Progress.CurrentFrame != CoreFrameKind.Fh)
            {
                bhSyncMs = sw.Elapsed.TotalMilliseconds;
            }

            if (status == ProgressiveDecodeStatus.Failed
                || status == ProgressiveDecodeStatus.Completed
                || (fhSyncMs >= 0 && bhSyncMs >= 0)
                || sw.Elapsed.TotalSeconds > 30)
            {
                break;
            }
        }

        sw.Stop();
        if (fhSyncMs < 0 || bhSyncMs < 0)
        {
            return -1;
        }

        return bhSyncMs;
    }

    private static string ResolveInputPngPath()
    {
        const string relative = "Onta_core/test/in_files/Sample1.png";
        var known = Path.Combine("C:", "proj", "Onta", relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(known))
        {
            return known;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"入力ファイルが見つかりません: {relative}");
    }
}
