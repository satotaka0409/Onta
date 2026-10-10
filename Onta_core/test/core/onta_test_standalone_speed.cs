using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// FH 未受信のまま BH+BD を単独復号したときの、BD 実測による速度比の検証です。
/// </summary>
public sealed class OntaTestStandaloneSpeed
{
    private const int SampleRate = 44100;

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 48,
        ModulationScheme: ModulationScheme.Qam16,
        SampleRate: SampleRate,
        ChannelMode: ChannelMode.Stereo,
        BlockInterleaveFactor: 1);

    /// <summary>
    /// 速度がずれた BH+BD を単独復号すると、受理した BD の受信長から速度比（受信長 ÷ 公称長）が求まること。
    /// </summary>
    [Fact]
    public void StandaloneBlock_MeasuresSpeedFromBlockData()
    {
        const double speed = 1.001;
        var codec = new FileWavCodec(Profile);
        var payload = new byte[8000];
        new Random(5).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var firstBh = events.FindIndex(x => x.Kind == TransmissionFrameKind.Bh);
        var start = (int)events[firstBh - 1].End;
        var playedLeft = Resample(left[start..], speed);
        var playedRight = Resample(right[start..], speed);
        var state = new ProgressiveDecodeState();
        var bhStart = (int)Math.Round(Profile.BlockHeaderUnmodulatedSamples / speed);

        var result = codec.DecodeStandaloneBlockProgressive(
            playedLeft,
            playedRight,
            state,
            bhStart,
            tuning: null,
            allowIncomplete: false,
            out _);

        Assert.Equal(StandaloneBlockStatus.Decoded, result);
        Assert.True(state.BlockBdOutcomeByIndex[0]);
        Assert.InRange(state.StandaloneMeasuredSpeedStep, (1.0 / speed) - 0.0002, (1.0 / speed) + 0.0002);
    }

    /// <summary>
    /// 送信波形を一定の速度比で再生した波形にします（線形補間。speed が 1 より大きいとテープが速く、受信長が短くなる）。
    /// </summary>
    /// <param name="source">送信波形。</param>
    /// <param name="speed">再生速度比。</param>
    /// <returns>再生した波形。</returns>
    private static Complex[] Resample(Complex[] source, double speed)
    {
        var count = (int)((source.Length - 2) / speed);
        var output = new Complex[count];
        for (var n = 0; n < count; n++)
        {
            var pos = n * speed;
            var i = (int)pos;
            var f = pos - i;
            output[n] = new Complex((source[i].Real * (1 - f)) + (source[i + 1].Real * f), 0);
        }

        return output;
    }

    /// <summary>
    /// ペイロードを送信波形へ符号化し、各フレームの終端サンプル位置も返します。
    /// </summary>
    /// <param name="codec">送信に使うコーデック。</param>
    /// <param name="payload">送るファイルの中身。</param>
    /// <returns>L/R 送信波形とフレームの終端位置。</returns>
    private static (Complex[] Left, Complex[] Right, List<(TransmissionFrameKind Kind, long End)> Events) Encode(
        FileWavCodec codec,
        byte[] payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta_test_standalone_speed_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, payload);
        try
        {
            long position = 0;
            var events = new List<(TransmissionFrameKind Kind, long End)>();
            var (left, right) = codec.EncodeFileToSamples(
                payload,
                new FileInfo(path),
                onFrameTransmitted: kind => events.Add((kind, position)),
                onPcmChunk: (l, _) => position += l.Length);
            return (left, right, events);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
