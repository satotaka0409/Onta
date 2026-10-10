using System.Numerics;
using System.Reflection;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.History;

/// <summary>
/// ブロックのみWAVを読み込んだ際に、親未解決ブロック(orphan)として保持されることを確認します。
/// </summary>
public sealed class OntaTestHistory1
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 16,
        ModulationScheme: ModulationScheme.Bpsk,
        ChannelMode: ChannelMode.Mono,
        BlockInterleaveFactor: 1);

    [Fact]
    public void Decode_BlockOnlyWav_RegistersOrphan()
    {
        // 1ブロック分のペイロードのみ（末尾短ブロックにしない）。
        var input = BuildPayload(size: 4096);
        var fileName = "history_orphan_input.bin";
        var tempDir = Path.Combine(Path.GetTempPath(), "onta_test_history");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, fileName);
        var wavPath = TestPaths.ResolveOutputPath("history_orphan_block_only.wav");
        File.WriteAllBytes(inputPath, input);

        var codec = new FileWavCodec(Profile);
        var fileInfo = new FileInfo(inputPath);

        var frameLeft = new List<(TransmissionFrameKind Kind, Complex[] Samples)>();
        var pending = new List<Complex>();
        _ = codec.EncodeFileToSamples(
            input,
            fileInfo,
            onFrameTransmitted: kind =>
            {
                frameLeft.Add((kind, pending.ToArray()));
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

        var bhFrames = frameLeft.Where(x => x.Kind == TransmissionFrameKind.Bh).ToArray();
        var bdFrames = frameLeft.Where(x => x.Kind == TransmissionFrameKind.Bd).ToArray();
        Assert.True(bhFrames.Length >= 1, "1ブロック分のBHが必要です。");
        Assert.True(bdFrames.Length >= 1, "1ブロック分のBDが必要です。");

        // BLK-0 の BH+BD のみを連結して block-only WAV を作る（FH 無し）。
        var blockOnly = Concat(bhFrames[0].Samples, bdFrames[0].Samples);
        WavWriter.WriteMono16(wavPath, Profile.SampleRate, blockOnly, Profile.SamplePeak);

        var (leftRead, rightRead) = WavReader.ReadPcm16(wavPath);
        Assert.Empty(rightRead);

        // UI と同じく FH 無しのまま復号し、standalone BH+BD → 不明ブロック登録を確認する。
        var state = new ProgressiveDecodeState();
        var status = codec.DecodePcmSamplesProgressive(
            leftRead,
            rightRead,
            state,
            correctWow: true,
            wowParams: null,
            tuning: DecodeRuntimeTuning.Default,
            allowIncomplete: false);

        Assert.Equal(ProgressiveDecodeStatus.Failed, status);
        Assert.False(state.HeaderReady);

        var orphanPayloadByHash = GetPropertyValue<Dictionary<string, byte[]>>(state, "OrphanPayloadByHash");
        var orphanDetailByHash = GetPropertyValue<Dictionary<string, string>>(state, "OrphanDetailByHash");

        Assert.NotNull(orphanPayloadByHash);
        Assert.NotNull(orphanDetailByHash);
        Assert.Single(orphanPayloadByHash);
        Assert.Single(orphanDetailByHash);
        Assert.Equal(4096, orphanPayloadByHash.Values.First().Length);

        var detail = orphanDetailByHash.Values.First();
        Assert.Contains("BH+BD index=0", detail);
    }

    private static byte[] BuildPayload(int size)
    {
        var payload = new byte[size];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 + 7);
        }

        return payload;
    }

    private static Complex[] Concat(Complex[] first, Complex[] second)
    {
        var merged = new Complex[first.Length + second.Length];
        Array.Copy(first, 0, merged, 0, first.Length);
        Array.Copy(second, 0, merged, first.Length, second.Length);
        return merged;
    }

    private static T GetPropertyValue<T>(object target, string propertyName) where T : class
    {
        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
        return (prop.GetValue(target) as T)
               ?? throw new InvalidOperationException($"Property '{propertyName}' was null or of unexpected type.");
    }
}
