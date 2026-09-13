using System.Numerics;
using System.Reflection;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests;

/// <summary>
/// ブロックのみWAVを読み込んだ際に、親未解決ブロック(orphan)として保持されることを確認します。
/// </summary>
public sealed class OntaTestHistory1
{
    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 8,
        ModulationScheme: ModulationScheme.Bpsk,
        ChannelMode: ChannelMode.Mono,
        BlockInterleaveFactor: 1);

    [Fact]
    public void Decode_BlockOnlyWav_RegistersOrphan()
    {
        var input = BuildPayload(size: 5000);
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
        Assert.True(bhFrames.Length >= 2, "2ブロック分のBHが必要です。");
        Assert.True(bdFrames.Length >= 2, "2ブロック分のBDが必要です。");

        // 2ブロック目(BLK-1)の BH+BD のみを連結して block-only WAV を作る。
        var blockOnly = Concat(bhFrames[1].Samples, bdFrames[1].Samples);
        WavWriter.WriteMono16(wavPath, Profile.SampleRate, blockOnly, Profile.SamplePeak);
        HistoryAssert.SaveSendAndAssertRegistered(nameof(Decode_BlockOnlyWav_RegistersOrphan), inputPath, wavPath);

        var (leftRead, rightRead) = WavReader.ReadPcm16(wavPath);
        Assert.Empty(rightRead);

        var state = new ProgressiveDecodeState();
        PrepareStateForBlockOnlyDecode(state, expectedBlockCount: 1, expectedFileSize: 4096);

        var status = codec.DecodePcmSamplesProgressive(
            leftRead,
            rightRead,
            state,
            correctWow: false,
            wowParams: null,
            tuning: DecodeRuntimeTuning.Default,
            allowIncomplete: false);

        Assert.Equal(ProgressiveDecodeStatus.Failed, status);

        var orphanPayloadByHash = GetPropertyValue<Dictionary<string, byte[]>>(state, "OrphanPayloadByHash");
        var orphanDetailByHash = GetPropertyValue<Dictionary<string, string>>(state, "OrphanDetailByHash");

        Assert.NotNull(orphanPayloadByHash);
        Assert.NotNull(orphanDetailByHash);
        Assert.NotEmpty(orphanPayloadByHash);
        Assert.NotEmpty(orphanDetailByHash);

        var detail = orphanDetailByHash.Values.First();
        Assert.Contains("孤立ブロック index=1", detail);
    }

    private static void PrepareStateForBlockOnlyDecode(ProgressiveDecodeState state, int expectedBlockCount, long expectedFileSize)
    {
        SetProperty(state, nameof(ProgressiveDecodeState.HeaderReady), true);
        SetProperty(state, nameof(ProgressiveDecodeState.BlockCount), expectedBlockCount);
        SetProperty(state, nameof(ProgressiveDecodeState.FileSize), expectedFileSize);
        SetField(state, "OutputSlots", new byte[expectedBlockCount][]);
        SetField(state, "SlotAccepted", new bool[expectedBlockCount]);
        SetField(state, "Pass", 0);
        SetField(state, "Local", 0);
        SetField(state, "WarpedCursor", 0);
        SetField(state, "LogicalOffset", 0L);
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

    private static void SetProperty<T>(object target, string propertyName, T value)
    {
        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
        prop.SetValue(target, value);
    }

    private static void SetField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new MissingFieldException(target.GetType().FullName, fieldName);
        field.SetValue(target, value);
    }

    private static T GetPropertyValue<T>(object target, string propertyName) where T : class
    {
        var prop = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                   ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
        return (prop.GetValue(target) as T)
               ?? throw new InvalidOperationException($"Property '{propertyName}' was null or of unexpected type.");
    }
}
