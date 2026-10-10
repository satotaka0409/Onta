using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// ライブ受信で録音開始とテープ再生がずれても、FH 手前の無変調区間の終端（FH 変調部の先頭）を検出できることを確認します。
/// </summary>
public sealed class OntaTest14
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 16,
        ModulationScheme: ModulationScheme.Qpsk,
        ChannelMode: ChannelMode.Mono,
        BlockInterleaveFactor: 1);

    /// <summary>
    /// 先頭に雑音が付いても、アンカーが送信どおりの FH 変調部先頭と一致すること。
    /// </summary>
    [Fact]
    public void Detect_WithNoisyLeadIn_FindsFileHeaderStart()
    {
        var codec = new FileWavCodec(Profile);
        var signal = Encode(codec);
        var lead = 44100 * 3;
        var input = WithLeadIn(signal, lead, noise: 0.01);

        var anchors = DetectAll(codec, input, chunk: input.Length);

        Assert.NotEmpty(anchors);
        Assert.InRange(anchors[0] - (lead + codec.FileHeaderDataOffsetSamples), -2, 2);
    }

    /// <summary>
    /// プリアンブルの途中から録音が始まっても、アンカーが FH 変調部先頭と一致すること。
    /// </summary>
    [Fact]
    public void Detect_StartingMidPreamble_FindsFileHeaderStart()
    {
        var codec = new FileWavCodec(Profile);
        var signal = Encode(codec);
        var skip = 44100;
        var input = signal[skip..];

        var anchors = DetectAll(codec, input, chunk: input.Length);

        Assert.NotEmpty(anchors);
        Assert.InRange(anchors[0] - (codec.FileHeaderDataOffsetSamples - skip), -2, 2);
    }

    /// <summary>
    /// 小分けに供給しても同じ位置を返し、BH の短い無変調区間はアンカーにしないこと（冒頭 FH と末尾 FH の 2 回だけ）。
    /// </summary>
    [Fact]
    public void Detect_ChunkedInput_IgnoresBlockHeaderGap()
    {
        var codec = new FileWavCodec(Profile);
        var signal = Encode(codec);
        var lead = 44100 / 2;
        var input = WithLeadIn(signal, lead, noise: 0.005);

        var anchors = DetectAll(codec, input, chunk: 4410);

        Assert.Equal(2, anchors.Count);
        Assert.InRange(anchors[0] - (lead + codec.FileHeaderDataOffsetSamples), -2, 2);
    }

    /// <summary>
    /// テープ速度がずれていても、無変調区間の周期から速度比を精密に求め、アンカー位置も合うこと。
    /// </summary>
    /// <param name="step">再生側 1 サンプルあたりの送信サンプル数（1 より大きいとテープが速い）。補正比はその逆数になる。</param>
    [Theory]
    [InlineData(0.97)]
    [InlineData(1.003)]
    [InlineData(1.03)]
    public void Detect_SpeedShifted_EstimatesSpeed(double step)
    {
        var codec = new FileWavCodec(Profile);
        var signal = Encode(codec);
        var played = TapeSpeedResampler.Resample(signal, signal.Length, step);
        var lead = 44100 * 2;
        var input = WithLeadIn(played, lead, noise: 0.01);
        var detector = codec.CreateAnchorDetector();

        Assert.True(detector.TryAdvance(input, out var anchor));
        Assert.InRange((detector.LastPeriod / detector.NominalPeriod) - (1.0 / step), -1e-4, 1e-4);
        Assert.InRange(anchor - (lead + (codec.FileHeaderDataOffsetSamples / step)), -2, 2);
    }

    /// <summary>
    /// 先頭余白と速度ずれのある WAV も、一括復号前の位置合わせでファイルを復元できること。
    /// </summary>
    /// <param name="step">再生側 1 サンプルあたりの送信サンプル数（1 より大きいとテープが速い）。</param>
    [Theory]
    [InlineData(0.97)]
    [InlineData(1.006)]
    [InlineData(1.03)]
    public void Decode_SpeedShiftedWithLeadIn_RestoresFile(double step)
    {
        var codec = new FileWavCodec(Profile);
        var payload = BuildPayload();
        var signal = Encode(codec, payload);
        var played = TapeSpeedResampler.Resample(signal, signal.Length, step);
        var left = WithLeadIn(played, 44100 * 2, noise: 0.003);
        var right = Array.Empty<Complex>();

        Assert.True(ReceiveAlignment.TryAlign(codec, ref left, ref right, out var speedStep));
        Assert.InRange(speedStep, (1.0 / step) - 1e-4, (1.0 / step) + 1e-4);

        var state = new ProgressiveDecodeState();
        var status = codec.DecodePcmSamplesProgressive(
            left,
            right,
            state,
            correctWow: true,
            tuning: DecodeRuntimeTuning.Default,
            allowIncomplete: false);

        Assert.Equal(ProgressiveDecodeStatus.Completed, status);
        Assert.Equal(payload, state.CompletedFile);
    }

    /// <summary>
    /// 実テープ相当（ワウ・速度ずれ・帯域制限・極性反転・先頭余白）のステレオ入力からファイルを復元できること。
    /// </summary>
    [Fact]
    public void Decode_RealisticTapeChannel_RestoresFile()
    {
        var codec = new FileWavCodec(new FileWavCodecProfile(
            ActiveSubcarriers: 16,
            ModulationScheme: ModulationScheme.Qpsk,
            ChannelMode: ChannelMode.Stereo,
            BlockInterleaveFactor: 1));
        var payload = new byte[3000];
        new Random(1).NextBytes(payload);
        var tempDir = Path.Combine(Path.GetTempPath(), "onta_test_core");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "core_tape_channel.bin");
        File.WriteAllBytes(inputPath, payload);
        var (signalLeft, signalRight) = codec.EncodeFileToSamples(payload, new FileInfo(inputPath));
        var left = ThroughTapeChannel(signalLeft, lead: 44100 * 3, seed: 5);
        var right = ThroughTapeChannel(signalRight, lead: 44100 * 3, seed: 6);

        Assert.True(ReceiveAlignment.TryAlign(codec, ref left, ref right, out _));

        var state = new ProgressiveDecodeState();
        var status = codec.DecodePcmSamplesProgressive(
            left,
            right,
            state,
            correctWow: true,
            tuning: DecodeRuntimeTuning.Default,
            allowIncomplete: false);

        Assert.Equal(ProgressiveDecodeStatus.Completed, status);
        Assert.Equal(payload, state.CompletedFile);
    }

    /// <summary>
    /// カセット再生を模した劣化（ワウ 1.1Hz/8.3Hz、0.5% 速い再生、7kHz 低域通過、40Hz 高域通過、極性反転、先頭余白と雑音）を加えます。
    /// </summary>
    /// <param name="signal">送信波形。</param>
    /// <param name="lead">先頭に付ける雑音のみの区間（サンプル）。</param>
    /// <param name="seed">雑音の乱数シード。</param>
    /// <returns>劣化後の受信波形。</returns>
    private static Complex[] ThroughTapeChannel(Complex[] signal, int lead, int seed)
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
                + (0.002 * Math.Sin((2 * Math.PI * 1.1 * t) + 0.7))
                + (0.001 * Math.Sin((2 * Math.PI * 8.3 * t) + 2.1));
        }

        var played = TapeSpeedResampler.Resample(
            wowed.Select(x => new Complex(x, 0.0)).ToArray(),
            wowed.Count,
            1.005);
        var samples = played.Select(c => c.Real).ToArray();
        LowPass(samples, 7000);
        HighPass(samples, 40);

        var rng = new Random(seed);
        var result = new Complex[lead + samples.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var noise = (rng.NextDouble() - 0.5) * 0.01;
            result[i] = new Complex(noise - (i >= lead ? samples[i - lead] : 0.0), 0.0);
        }

        return result;
    }

    /// <summary>
    /// 2 段 RC の低域通過に直達成分を 15% 混ぜ、テープの高域落ちを模します。
    /// </summary>
    /// <param name="x">対象サンプル（上書き）。</param>
    /// <param name="cutoff">カットオフ周波数（Hz）。</param>
    private static void LowPass(double[] x, double cutoff)
    {
        var dt = 1.0 / 44100;
        var alpha = dt / ((1.0 / (2.0 * Math.PI * cutoff)) + dt);
        var y1 = 0.0;
        var y2 = 0.0;
        for (var i = 0; i < x.Length; i++)
        {
            y1 += alpha * (x[i] - y1);
            y2 += alpha * (y1 - y2);
            x[i] = (0.15 * x[i]) + (0.85 * y2);
        }
    }

    /// <summary>
    /// 1 段 RC の高域通過で、ライン入力の DC カットを模します。
    /// </summary>
    /// <param name="x">対象サンプル（上書き）。</param>
    /// <param name="cutoff">カットオフ周波数（Hz）。</param>
    private static void HighPass(double[] x, double cutoff)
    {
        var rc = 1.0 / (2.0 * Math.PI * cutoff);
        var a = rc / (rc + (1.0 / 44100));
        var prevX = 0.0;
        var prevY = 0.0;
        for (var i = 0; i < x.Length; i++)
        {
            var y = a * (prevY + x[i] - prevX);
            prevX = x[i];
            prevY = y;
            x[i] = y;
        }
    }

    /// <summary>
    /// テスト用の固定ペイロードを作ります。
    /// </summary>
    private static byte[] BuildPayload()
    {
        var payload = new byte[2000];
        new Random(3).NextBytes(payload);
        return payload;
    }

    /// <summary>
    /// 1 ブロック分のファイルを送信波形（L）へ変換します。
    /// </summary>
    private static Complex[] Encode(FileWavCodec codec) => Encode(codec, BuildPayload());

    /// <summary>
    /// 指定ペイロードのファイルを送信波形（L）へ変換します。
    /// </summary>
    private static Complex[] Encode(FileWavCodec codec, byte[] payload)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "onta_test_core");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "core_anchor.bin");
        File.WriteAllBytes(inputPath, payload);
        var (left, _) = codec.EncodeFileToSamples(payload, new FileInfo(inputPath));
        return left;
    }

    /// <summary>
    /// 先頭に雑音区間を付け、全体へ弱い雑音を重ねます。
    /// </summary>
    private static Complex[] WithLeadIn(Complex[] signal, int lead, double noise)
    {
        var rng = new Random(11);
        var result = new Complex[lead + signal.Length];
        for (var i = 0; i < result.Length; i++)
        {
            var n = (rng.NextDouble() - 0.5) * 2.0 * noise;
            result[i] = new Complex(n + (i >= lead ? signal[i - lead].Real : 0.0), 0.0);
        }

        return result;
    }

    /// <summary>
    /// 入力を指定チャンクずつ増やしながら検出器へ渡し、検出したアンカーを全て返します。
    /// </summary>
    private static List<int> DetectAll(FileWavCodec codec, Complex[] input, int chunk)
    {
        var detector = codec.CreateAnchorDetector();
        var anchors = new List<int>();
        for (var length = Math.Min(chunk, input.Length); ; length = Math.Min(length + chunk, input.Length))
        {
            while (detector.TryAdvance(input.AsSpan(0, length), out var anchor))
            {
                anchors.Add(anchor);
            }

            if (length == input.Length)
            {
                break;
            }
        }

        return anchors;
    }
}
