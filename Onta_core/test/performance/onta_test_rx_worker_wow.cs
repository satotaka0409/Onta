using System.Numerics;
using Onta.Core;
using Onta.Performance;
using Xunit;

namespace Onta.Core.Tests.Performance;

/// <summary>
/// 性能測定受信ワーカーのワウ基準（WAV 入力で受信処理を通す）です。
/// </summary>
public sealed class OntaTestPerformanceRxWorkerWow
{
    /// <summary>
    /// 受信の「変調」が ON（SC-24）でも、受信信号が 3 kHz の単一トーンならトーン 3000 Hz を基準にし、ワウがほぼ 0 になることを確認します。
    /// 以前は最寄りの OFDM キャリア（L 3012.9 Hz / R 2901.0 Hz）を基準にして L −0.43% / R +1.5% と出ていました。
    /// </summary>
    [Fact]
    public void ModulatedReceive_SingleTone_LocksToToneFrequency()
    {
        const int sampleRate = 48000;
        var path = Path.Combine(Path.GetTempPath(), $"onta_perf_wow_{Guid.NewGuid():N}.wav");
        try
        {
            var frames = sampleRate * 3 / 2;
            var tone = new Complex[frames];
            for (var i = 0; i < frames; i++)
            {
                tone[i] = new Complex(0.5 * Math.Sin(2.0 * Math.PI * 3000.0 * i / sampleRate), 0.0);
            }

            WavWriter.WriteStereo16(path, sampleRate, tone, tone, peakTarget: 0.5);

            using var worker = new PerformanceRxWorker();
            Assert.True(worker.TryStart(new PerformanceRxSettings(
                ChannelMode: ChannelMode.Stereo,
                UseWavInput: true,
                WavPath: path,
                InputDeviceNumber: -1,
                InputGain: 1.0,
                ActiveSubcarriers: 24,
                ModulationScheme: ModulationScheme.Qpsk,
                CaptureConstellation: true,
                SignalMode: PerformanceSignalMode.Modulated)));

            // 全テストの並列実行中は CPU を取られて受信が遅れるため、余裕を持たせる
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (worker.IsBusy && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }

            Assert.False(worker.IsBusy);
            worker.GetWowView(out var left, out var right);
            Assert.Equal(PerformanceWowReferenceKind.Tone, left.Kind);
            Assert.Equal(PerformanceWowReferenceKind.Tone, right.Kind);
            Assert.Equal(3000.0, left.ReferenceHz);
            Assert.Equal(3000.0, right.ReferenceHz);

            var status = worker.SharedStatus.Read();
            Assert.InRange(status.WowLeftPercent, -0.01, 0.01);
            Assert.InRange(status.WowRightPercent, -0.01, 0.01);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
