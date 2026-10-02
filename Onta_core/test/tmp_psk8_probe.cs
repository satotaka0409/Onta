using System.Numerics;
using Onta.Core;
using Xunit;
using Xunit.Abstractions;

namespace Onta.Core.Tests;

public sealed class TmpPsk8Probe
{
    private readonly ITestOutputHelper _out;

    public TmpPsk8Probe(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(ModulationScheme.Psk8, 0.0, 1.0, false, false, 0.0)]
    [InlineData(ModulationScheme.Psk8, 1.0, 1.0, false, false, 0.0)]
    [InlineData(ModulationScheme.Psk8, 2.0, 1.005, true, true, 0.0)]
    [InlineData(ModulationScheme.Psk8, 0.0, 1.005, false, false, 0.0)]
    [InlineData(ModulationScheme.Psk8, 0.0, 1.0, true, false, 0.0)]
    [InlineData(ModulationScheme.Psk8, 0.0, 1.0, false, false, 0.03)]
    [InlineData(ModulationScheme.Qpsk, 2.0, 1.005, true, true, 0.0)]
    public void Batch(ModulationScheme mod, double wowScale, double speed, bool lpf, bool invert, double crosstalk)
    {
        var codec = new FileWavCodec(new FileWavCodecProfile(16, mod, ChannelMode: ChannelMode.Stereo, BlockInterleaveFactor: 1));
        var payload = new byte[9544];
        new Random(1).NextBytes(payload);
        var dir = Path.Combine(Path.GetTempPath(), "onta_test_core");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "psk8.bin");
        File.WriteAllBytes(path, payload);
        var (sl, sr) = codec.EncodeFileToSamples(payload, new FileInfo(path));
        if (crosstalk > 0)
        {
            var l0 = sl.ToArray();
            for (var i = 0; i < sl.Length; i++)
            {
                sl[i] += crosstalk * sr[i];
                sr[i] += crosstalk * l0[i];
            }
        }

        var l = Channel(sl, wowScale, speed, lpf, invert, 5);
        var r = Channel(sr, wowScale, speed, lpf, invert, 6);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var aligned = ReceiveAlignment.TryAlign(codec, ref l, ref r, out var step);
        var state = new ProgressiveDecodeState();
        var status = codec.DecodePcmSamplesProgressive(l, r, state, correctWow: true, tuning: DecodeRuntimeTuning.Default, allowIncomplete: false);
        _out.WriteLine($"mod={mod} wow={wowScale} speed={speed} lpf={lpf} inv={invert} xt={crosstalk} aligned={aligned} step={step:F5} status={status} blk={state.AcceptedBlockCount}/{state.BlockCount} ok={(state.CompletedFile is not null && state.CompletedFile.AsSpan().SequenceEqual(payload))} err={state.LastError} elapsed={sw.Elapsed.TotalSeconds:F1}s");
    }

    [Theory]
    [InlineData(ModulationScheme.Psk8, 0.0)]
    [InlineData(ModulationScheme.Psk8, 0.5)]
    [InlineData(ModulationScheme.Psk8, 0.35)]
    [InlineData(ModulationScheme.Psk8, 0.25)]
    [InlineData(ModulationScheme.Qpsk, 0.35)]
    [InlineData(ModulationScheme.Qpsk, 0.25)]
    [InlineData(ModulationScheme.Psk8, -0.02)]
    [InlineData(ModulationScheme.Psk8, -0.04)]
    [InlineData(ModulationScheme.Qpsk, -0.04)]
    public void Level(ModulationScheme mod, double clipRatio)
    {
        var codec = new FileWavCodec(new FileWavCodecProfile(16, mod, ChannelMode: ChannelMode.Stereo, BlockInterleaveFactor: 1));
        var payload = new byte[9544];
        new Random(1).NextBytes(payload);
        var dir = Path.Combine(Path.GetTempPath(), "onta_test_core");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "psk8.bin");
        File.WriteAllBytes(path, payload);
        var (sl, sr) = codec.EncodeFileToSamples(payload, new FileInfo(path));
        var peak = sl.Max(c => Math.Abs(c.Real));
        var rms = Math.Sqrt(sl.Skip(44100 * 4).Take(44100 * 10).Average(c => c.Real * c.Real));
        var rng = new Random(9);
        Complex[] Apply(Complex[] x)
        {
            var y = new Complex[x.Length];
            for (var i = 0; i < x.Length; i++)
            {
                var v = x[i].Real;
                if (clipRatio > 0)
                {
                    var c = peak * clipRatio;
                    v = Math.Clamp(v, -c, c);
                }
                else if (clipRatio < 0)
                {
                    v += (rng.NextDouble() - 0.5) * 2 * Math.Sqrt(3) * -clipRatio;
                }

                y[i] = v;
            }

            return y;
        }

        var l = Channel(Apply(sl), 1.0, 1.003, true, false, 5);
        var r = Channel(Apply(sr), 1.0, 1.003, true, false, 6);
        var aligned = ReceiveAlignment.TryAlign(codec, ref l, ref r, out var step);
        var state = new ProgressiveDecodeState();
        var status = codec.DecodePcmSamplesProgressive(l, r, state, correctWow: true, tuning: DecodeRuntimeTuning.Default, allowIncomplete: false);
        _out.WriteLine($"mod={mod} clip={clipRatio} peak={peak:F3} rms={rms:F4} status={status} blk={state.AcceptedBlockCount}/{state.BlockCount} ok={(state.CompletedFile is not null && state.CompletedFile.AsSpan().SequenceEqual(payload))} err={state.LastError}");
    }

    private static Complex[] Channel(Complex[] signal, double wowScale, double speed, bool lpf, bool invert, int seed)
    {
        var wowed = new List<double>(signal.Length);
        var position = 0.0;
        for (var n = 0; position < signal.Length - 1; n++)
        {
            var i = (int)position;
            var frac = position - i;
            wowed.Add((signal[i].Real * (1 - frac)) + (signal[i + 1].Real * frac));
            var t = n / 44100.0;
            position += 1.0
                + (wowScale * 0.001 * Math.Sin((2 * Math.PI * 1.1 * t) + 0.7))
                + (wowScale * 0.0005 * Math.Sin((2 * Math.PI * 8.3 * t) + 2.1));
        }

        var played = TapeSpeedResampler.Resample(wowed.Select(x => new Complex(x, 0.0)).ToArray(), wowed.Count, speed);
        var samples = played.Select(c => c.Real).ToArray();
        if (lpf)
        {
            var dt = 1.0 / 44100;
            var alpha = dt / ((1.0 / (2.0 * Math.PI * 7000)) + dt);
            var y1 = 0.0;
            var y2 = 0.0;
            for (var i = 0; i < samples.Length; i++)
            {
                y1 += alpha * (samples[i] - y1);
                y2 += alpha * (y1 - y2);
                samples[i] = (0.15 * samples[i]) + (0.85 * y2);
            }
        }

        var sign = invert ? -1.0 : 1.0;
        var rng = new Random(seed);
        var lead = 44100 * 3;
        var result = new Complex[lead + samples.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = new Complex(((rng.NextDouble() - 0.5) * 0.01) + (i >= lead ? sign * samples[i - lead] : 0.0), 0.0);
        }

        return result;
    }
}
