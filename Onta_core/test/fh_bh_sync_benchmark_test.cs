using System.Diagnostics;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace Onta.Core.Tests;

/// <summary>
/// 同一WAVに対して、受信開始から FH/BH 同期までの経過時間を計測します。
/// 段階デコードを prefix 拡張で繰り返すため通常の単体テスト実行に混ぜると長時間化しやすく、
/// ベンチ専用プロジェクトでの実行を前提とします。
/// </summary>
public sealed class FhBhSyncBenchmarkTest
{
    private readonly ITestOutputHelper _output;

    public FhBhSyncBenchmarkTest(ITestOutputHelper output)
    {
        _output = output;
    }

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 16,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Stereo);

    [Fact(Skip = "ベンチマーク用途のため通常テスト実行ではスキップします。計測は Onta_core/bench の専用プロジェクトを使用してください。")]
    public void Measure_FhBhSync_Time_FromReceiveStart()
    {
        var inputPath = TestPaths.ResolveInputPng();
        var wavPath = TestPaths.ResolveOutputPath("fh_bh_sync_benchmark.wav");
        var benchInputPath = TestPaths.ResolveOutputPath("fh_bh_sync_benchmark_input.bin");

        var sourcePayload = File.ReadAllBytes(inputPath);
        var payload = sourcePayload.Length > 2 * 1024
            ? sourcePayload.AsSpan(0, 2 * 1024).ToArray()
            : sourcePayload;
        File.WriteAllBytes(benchInputPath, payload);

        var codecForEncode = new FileWavCodec(Profile);
        var (leftTx, rightTx) = codecForEncode.EncodeFileToSamples(payload, new FileInfo(benchInputPath));
        WavWriter.WriteStereo16(wavPath, Profile.SampleRate, leftTx, rightTx, Profile.SamplePeak);

        var (leftRx, rightRx) = WavReader.ReadPcm16(wavPath);

        var codec = new FileWavCodec(Profile);
        var state = new ProgressiveDecodeState();
        var chunkSize = Math.Max(Profile.SampleRate * 2, 8192);

        var fhSyncMs = -1.0;
        var bhSyncMs = -1.0;
        var cursor = 0;
        var sw = Stopwatch.StartNew();

        var maxIterations = (leftRx.Length / chunkSize) + 8;
        var iteration = 0;
        while (cursor < leftRx.Length && iteration < maxIterations)
        {
            iteration++;
            cursor = Math.Min(leftRx.Length, cursor + chunkSize);

            var leftChunk = new Complex[cursor];
            Array.Copy(leftRx, 0, leftChunk, 0, cursor);

            var rightChunk = new Complex[cursor];
            if (rightRx.Length > 0)
            {
                Array.Copy(rightRx, 0, rightChunk, 0, Math.Min(cursor, rightRx.Length));
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

            if (status == ProgressiveDecodeStatus.Failed)
            {
                throw new InvalidOperationException(state.LastError ?? "progressive decode failed");
            }

            if (fhSyncMs >= 0 && bhSyncMs >= 0)
            {
                break;
            }

            if (status == ProgressiveDecodeStatus.Completed)
            {
                break;
            }
        }

        sw.Stop();

        Assert.True(fhSyncMs >= 0, "FH sync time was not captured.");
        Assert.True(bhSyncMs >= 0, "BH sync time was not captured.");

        var hwIntrinsicEnv = Environment.GetEnvironmentVariable("DOTNET_EnableHWIntrinsic") ?? "(default)";
        var simdMode = hwIntrinsicEnv == "0" ? "Scalar" : "SimdOrScalarFallback";
        var summary =
            $"[SYNC-BENCH] mode={simdMode} env.DOTNET_EnableHWIntrinsic={hwIntrinsicEnv} fhMs={fhSyncMs:F3} bhMs={bhSyncMs:F3} totalMs={sw.Elapsed.TotalMilliseconds:F3}";

        _output.WriteLine(summary);
        Console.WriteLine(summary);
    }
}
