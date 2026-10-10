using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// 送信側の音切れ（信号の途中に挿入された 10 ms の無音）を受信側で詰めて復号できることを確認します。
/// </summary>
public sealed class OntaTest15
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 16,
        ModulationScheme: ModulationScheme.Psk8,
        ChannelMode: ChannelMode.Stereo,
        BlockInterleaveFactor: 1);

    /// <summary>
    /// ブロックデータの途中に無音が挿入されても、詰めれば元のファイルを復元できること。
    /// </summary>
    [Fact]
    public void Decode_WithInsertedSilence_RestoresFile()
    {
        var codec = new FileWavCodec(Profile);
        var payload = BuildPayload();
        var (left, right) = Encode(codec, payload);
        var gapPositions = new[] { left.Length * 2 / 5, left.Length * 3 / 5 };
        left = InsertSilence(left, gapPositions, seed: 1);
        right = InsertSilence(right, gapPositions, seed: 2);

        (left, right, var removed) = InputSilenceGapRemover.RemoveGaps(left, right, 44100);
        Assert.Equal(gapPositions.Length, removed);

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
    /// 音切れのない送信波形（先頭・末尾の無音を含む）からは何も取り除かないこと。
    /// </summary>
    [Fact]
    public void RemoveGaps_WithoutDropout_KeepsSignal()
    {
        var codec = new FileWavCodec(Profile);
        var (left, right) = Encode(codec, BuildPayload());
        left = AddNoise(left, seed: 3);
        right = AddNoise(right, seed: 4);

        var (outLeft, outRight, removed) = InputSilenceGapRemover.RemoveGaps(left, right, 44100);

        Assert.Equal(0, removed);
        Assert.Equal(left.Length, outLeft.Length);
        Assert.Equal(right.Length, outRight.Length);
    }

    /// <summary>
    /// チャンクに分けて流しても、一括と同じだけ無音を詰めること。
    /// </summary>
    [Fact]
    public void Process_Chunked_MatchesBatch()
    {
        var codec = new FileWavCodec(Profile);
        var (left, right) = Encode(codec, BuildPayload());
        var gapPositions = new[] { left.Length / 3, left.Length / 2 };
        left = InsertSilence(left, gapPositions, seed: 5);
        right = InsertSilence(right, gapPositions, seed: 6);
        var (batchLeft, _, _) = InputSilenceGapRemover.RemoveGaps(left, right, 44100);

        var remover = new InputSilenceGapRemover(44100, stereo: true);
        var chunkedLeft = new List<Complex>();
        for (var pos = 0; pos < left.Length; pos += 2205)
        {
            var n = Math.Min(2205, left.Length - pos);
            remover.Process(left.AsSpan(pos, n), right.AsSpan(pos, n), out var outLeft, out _);
            chunkedLeft.AddRange(outLeft);
        }

        remover.Flush(out var tailLeft, out _);
        chunkedLeft.AddRange(tailLeft);

        Assert.Equal(gapPositions.Length, remover.RemovedGapCount);
        Assert.Equal(batchLeft, chunkedLeft.ToArray());
    }

    /// <summary>
    /// テスト用の固定ペイロード（2 ブロック）を作ります。
    /// </summary>
    private static byte[] BuildPayload()
    {
        var payload = new byte[9544];
        new Random(7).NextBytes(payload);
        return payload;
    }

    /// <summary>
    /// ファイルを送信波形（L/R）へ変換し、逐次出力と同じ送出ゲインを掛けます。
    /// </summary>
    private static (Complex[] Left, Complex[] Right) Encode(FileWavCodec codec, byte[] payload)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "onta_test_core");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "core_gap.bin");
        File.WriteAllBytes(inputPath, payload);
        var (left, right) = codec.EncodeFileToSamples(payload, new FileInfo(inputPath));
        return (Scale(left), Scale(right));
    }

    /// <summary>
    /// 送出ゲインを掛けた複製を返します。
    /// </summary>
    private static Complex[] Scale(Complex[] x) => x.Select(v => v * FileWavCodec.TransmitOutputGain).ToArray();

    /// <summary>
    /// 指定位置へ 441 サンプル（10 ms）の無音を挿入し、全体へ雑音の床を重ねます。
    /// </summary>
    private static Complex[] InsertSilence(Complex[] signal, int[] positions, int seed)
    {
        var result = new List<Complex>(signal.Length + (positions.Length * 441));
        var last = 0;
        foreach (var position in positions)
        {
            result.AddRange(signal.AsSpan(last, position - last).ToArray());
            result.AddRange(Enumerable.Repeat(Complex.Zero, 441));
            last = position;
        }

        result.AddRange(signal.AsSpan(last).ToArray());
        return AddNoise(result.ToArray(), seed);
    }

    /// <summary>
    /// ライン入力相当の雑音の床（RMS 約 0.0002）を重ねた複製を返します。
    /// </summary>
    private static Complex[] AddNoise(Complex[] signal, int seed)
    {
        var rng = new Random(seed);
        return signal.Select(v => new Complex(v.Real + ((rng.NextDouble() - 0.5) * 0.0007), 0.0)).ToArray();
    }
}
