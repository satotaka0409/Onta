using System.Numerics;
using Onta.Core;
using Onta.Performance;
using Xunit;

namespace Onta.Core.Tests.Performance;

/// <summary>
/// 性能測定受信ワーカーの I-Q 表示を、低レベルの間は止める（直前の点を残す）ことの検証です。
/// </summary>
public sealed class OntaTestPerformanceRxWorkerIqHold
{
    /// <summary>
    /// L に QPSK を流したあと低レベルの雑音（−80 dBFS）だけになっても、L の I-Q は QPSK の点のまま残り、
    /// 雑音だけの R には点を出さないこと。以前は雑音を等化した点がグラフ全体に散らばっていた。
    /// </summary>
    [Fact]
    public void LowLevelInput_KeepsLastConstellation()
    {
        var sampleRate = PerformanceSignalGenerator.SampleRate;
        var path = Path.Combine(Path.GetTempPath(), $"onta_perf_iq_{Guid.NewGuid():N}.wav");
        try
        {
            var (signal, _) = PerformanceSignalGenerator.GenerateModulated(
                24, ModulationScheme.Qpsk, ChannelMode.Mono, durationSeconds: 1.5, amplitude: 0.5, sampleRate: sampleRate);
            var frames = signal.Length + sampleRate;
            var rng = new Random(1);
            var left = new Complex[frames];
            var right = new Complex[frames];
            for (var i = 0; i < frames; i++)
            {
                var noiseL = 1e-4 * (rng.NextDouble() * 2.0 - 1.0);
                var noiseR = 1e-4 * (rng.NextDouble() * 2.0 - 1.0);
                left[i] = i < signal.Length ? signal[i] : new Complex(noiseL, 0.0);
                right[i] = new Complex(noiseR, 0.0);
            }

            WavWriter.WriteStereo16(path, sampleRate, left, right, peakTarget: 0.5);

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
            var iq = worker.SharedStatus.Read().IqGraph;
            Assert.NotEmpty(iq.Points);
            Assert.Equal(iq.Points.Count, iq.LeftPointCount);

            // QPSK の点は振幅がそろう。雑音を等化した点は振幅がばらつく（レイリー分布で変動係数 約 0.5）。
            var magnitudes = iq.Points.Select(p => Math.Sqrt(p.I * p.I + p.Q * p.Q)).ToArray();
            var mean = magnitudes.Average();
            var std = Math.Sqrt(magnitudes.Select(m => (m - mean) * (m - mean)).Average());
            Assert.True(mean > 0);
            Assert.InRange(std / mean, 0.0, 0.15);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
