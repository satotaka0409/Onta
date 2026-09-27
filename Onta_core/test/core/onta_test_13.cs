using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// ファイルヘッダーが無いブロックだけを受信し、不明ブロックとして保持することを確認します。
/// </summary>
public sealed class OntaTest13
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 16,
        ModulationScheme: ModulationScheme.Bpsk,
        ChannelMode: ChannelMode.Mono,
        BlockInterleaveFactor: 1);

    /// <summary>
    /// BH+BD のみの WAV を復号すると、ファイルは復元せずペイロードを不明ブロックへ登録すること。
    /// </summary>
    [Fact]
    public void Decode_BlockOnlyWav_KeepsPayloadAsUnknownBlock()
    {
        var input = BuildPayload(size: 4096);
        var tempDir = Path.Combine(Path.GetTempPath(), "onta_test_core");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "core_unknown_block.bin");
        var wavPath = TestPaths.ResolveOutputPath("core_unknown_block.wav");
        File.WriteAllBytes(inputPath, input);

        var codec = new FileWavCodec(Profile);
        var frames = new List<(TransmissionFrameKind Kind, Complex[] Samples)>();
        var pending = new List<Complex>();
        _ = codec.EncodeFileToSamples(
            input,
            new FileInfo(inputPath),
            onFrameTransmitted: kind =>
            {
                frames.Add((kind, pending.ToArray()));
                pending.Clear();
            },
            onPcmChunk: (left, _) =>
            {
                for (var i = 0; i < left.Length; i++)
                {
                    pending.Add(left[i]);
                }
            },
            retainAllSamples: false);

        var blockHeader = frames.Where(frame => frame.Kind == TransmissionFrameKind.Bh).ToArray();
        var blockData = frames.Where(frame => frame.Kind == TransmissionFrameKind.Bd).ToArray();
        Assert.NotEmpty(blockHeader);
        Assert.NotEmpty(blockData);
        Assert.Contains(frames, frame => frame.Kind == TransmissionFrameKind.Fh);

        var blockOnly = Concat(blockHeader[0].Samples, blockData[0].Samples);
        WavWriter.WriteMono16(wavPath, Profile.SampleRate, blockOnly, Profile.SamplePeak);
        var (left, right) = WavReader.ReadPcm16(wavPath);
        Assert.Empty(right);

        var headerSeen = false;
        var state = new ProgressiveDecodeState
        {
            FileHeaderReady = (_, _, _, _, _) => headerSeen = true
        };
        var status = codec.DecodePcmSamplesProgressive(
            left,
            right,
            state,
            correctWow: true,
            wowParams: null,
            tuning: DecodeRuntimeTuning.Default,
            allowIncomplete: false);

        Assert.Equal(ProgressiveDecodeStatus.Failed, status);
        Assert.False(headerSeen);
        Assert.False(state.HeaderReady);
        Assert.False(state.Completed);
        Assert.Null(state.CompletedFile);

        Assert.Single(state.OrphanPayloadByHash);
        Assert.Single(state.OrphanDetailByHash);
        var payload = state.OrphanPayloadByHash.Values.Single();
        Assert.Equal(input, payload);
        Assert.Contains("BH+BD index=0", state.OrphanDetailByHash.Values.Single());
        Assert.True(state.BlockBdOutcomeByIndex.TryGetValue(0, out var accepted) && accepted);
        Assert.Equal(4, state.OrphanDataModulationByHash.Values.Single().Length);
    }

    /// <summary>
    /// 1 ブロックに収まる試験用ペイロードを作ります。
    /// </summary>
    /// <param name="size">バイト数。</param>
    /// <returns>決定的なバイト列。</returns>
    private static byte[] BuildPayload(int size)
    {
        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }

        return payload;
    }

    /// <summary>
    /// 2 つの複素サンプル列を連結します。
    /// </summary>
    /// <param name="first">前段。</param>
    /// <param name="second">後段。</param>
    /// <returns>連結結果。</returns>
    private static Complex[] Concat(Complex[] first, Complex[] second)
    {
        var merged = new Complex[first.Length + second.Length];
        Array.Copy(first, 0, merged, 0, first.Length);
        Array.Copy(second, 0, merged, first.Length, second.Length);
        return merged;
    }
}
