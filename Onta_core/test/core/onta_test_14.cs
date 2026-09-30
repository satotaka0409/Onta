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
    /// 1 ブロック分のファイルを送信波形（L）へ変換します。
    /// </summary>
    private static Complex[] Encode(FileWavCodec codec)
    {
        var payload = new byte[2000];
        new Random(3).NextBytes(payload);
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
        var detector = new PreambleAnchorDetector(
            codec.HeaderSymbolSamples,
            (44100 * 8) / 10,
            codec.CreateHeaderUnmodulatedSymbols());
        detector.Reset();
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
