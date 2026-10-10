using System.Diagnostics;
using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// リアルタイム受信で、1 回目の再生で NG になったブロックを、同じ受信のままテープを再生し直して回復できることを確認します。
/// </summary>
[Collection(RealtimeTestCollection.Name)]
public sealed class OntaTestReread
{
    private const int SampleRate = 44100;
    private const double FeedSpeed = 4.0;

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 48,
        ModulationScheme: ModulationScheme.Qam16,
        SampleRate: SampleRate,
        ChannelMode: ChannelMode.Stereo,
        BlockInterleaveFactor: 1);

    /// <summary>
    /// 1 回目は BD-1 を壊して NG にし、末尾 FH まで読んだあと 2 回目を頭から流すと、BLK-1 も受けてファイルが完成すること。
    /// </summary>
    [Fact]
    public void Live_RereadAfterTrailingFileHeader_RecoversNgBlock()
    {
        var codec = new FileWavCodec(Profile);
        var payload = new byte[(FileWavCodec.DataBlockBytes * 2) + 500];
        new Random(3).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var (damagedLeft, damagedRight) = DamageDataBlock(left, right, events, blockOrdinal: 1);

        var (decoded, state) = RunLive(codec, [(damagedLeft, damagedRight), (left, right)]);

        Assert.Equal(3, state.BlockCount);
        Assert.Equal(
            new[] { (0, true), (1, true), (2, true) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
        Assert.Equal(payload, decoded);
    }

    /// <summary>
    /// BD-1 だけが壊れた再生では、BLK-1 だけが NG になり、直後の BLK-2 は OK になること。
    /// </summary>
    [Fact]
    public void Live_DamagedDataBlock_DoesNotFailNextBlock()
    {
        var codec = new FileWavCodec(Profile);
        var payload = new byte[(FileWavCodec.DataBlockBytes * 2) + 500];
        new Random(3).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var (damagedLeft, damagedRight) = DamageDataBlock(left, right, events, blockOrdinal: 1);

        var (decoded, state) = RunLive(codec, [(damagedLeft, damagedRight)]);

        Assert.Null(decoded);
        Assert.Equal(
            new[] { (0, true), (1, false), (2, true) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
    }

    /// <summary>
    /// BD-1 の途中のサンプルが欠けて（録音の取りこぼし）公称長より短い再生でも、BLK-1 だけが NG になり、早く来た BLK-2 の BH を拾って OK になること。
    /// </summary>
    [Fact]
    public void Live_DataBlockMissingSamples_DoesNotSkipNextBlock()
    {
        var codec = new FileWavCodec(Profile);
        var payload = new byte[(FileWavCodec.DataBlockBytes * 2) + 500];
        new Random(3).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var (cutLeft, cutRight) = RemoveSamplesInDataBlock(left, right, events, blockOrdinal: 1, removeSeconds: 0.6);

        var (decoded, state) = RunLive(codec, [(cutLeft, cutRight)]);

        Assert.Null(decoded);
        Assert.Equal(
            new[] { (0, true), (1, false), (2, true) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
    }

    /// <summary>
    /// FH を受けずに BH-0 から受信し（BH 単独受信）、BD-1 の途中のサンプルが欠けていても、BLK-1 だけが NG になり BLK-2 は OK になること。
    /// </summary>
    [Fact]
    public void Live_StandaloneDataBlockMissingSamples_DoesNotSkipNextBlock()
    {
        var codec = new FileWavCodec(Profile);
        var payload = new byte[(FileWavCodec.DataBlockBytes * 2) + 500];
        new Random(3).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var (cutLeft, cutRight) = RemoveSamplesInDataBlock(left, right, events, blockOrdinal: 1, removeSeconds: 0.6);
        var firstBh = events.FindIndex(x => x.Kind == TransmissionFrameKind.Bh);
        var start = (int)events[firstBh - 1].End;

        var (decoded, state) = RunLive(codec, [(cutLeft[start..], cutRight[start..])]);

        Assert.Null(decoded);
        Assert.Equal(
            new[] { (0, true), (1, false), (2, true) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
    }

    /// <summary>
    /// BD-1 の途中で受信レベルがほぼ 0 になったら、BD の公称長が届くのを待たずに BLK-1 を NG にし、ヘッダー受信の待機（再生し直し待ち）に入ること。
    /// </summary>
    [Fact]
    public void Live_SignalLostDuringDataBlock_MarksNgWithoutWaitingForBlockEnd()
    {
        var codec = new FileWavCodec(Profile);
        var payload = new byte[(FileWavCodec.DataBlockBytes * 2) + 500];
        new Random(3).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var (bdStart, bdEnd) = FindDataBlockRange(events, blockOrdinal: 1);
        var cut = (bdStart + bdEnd) / 2;
        var silence = SampleRate * 3 / 2;
        Assert.True(silence < bdEnd - cut, "無音が BD の残りより長いと公称長が届いてしまう");
        Complex[] lostLeft = [.. left[..cut], .. new Complex[silence]];
        Complex[] lostRight = [.. right[..cut], .. new Complex[silence]];

        var (decoded, state) = RunLive(codec, [(lostLeft, lostRight)], notifyInputCompleted: false);

        Assert.Null(decoded);
        Assert.Equal(
            new[] { (0, true), (1, false) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
        Assert.True(state.AwaitingReread, "ヘッダー受信の待機に入っていない");
    }

    /// <summary>
    /// BD-1 の後半で信号が 3 秒消え、BH-2 から戻ったとき、BLK-1 だけが NG になり BLK-2 は OK になること。
    /// </summary>
    [Fact]
    public void Live_SignalLostDuringDataBlock_ReceivesNextBlockAfterReturn()
    {
        var codec = new FileWavCodec(Profile);
        var payload = new byte[(FileWavCodec.DataBlockBytes * 2) + 500];
        new Random(3).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var (lostLeft, lostRight) = ReplaceDataBlockTailWithSilence(left, right, events, blockOrdinal: 1, silenceSeconds: 3);

        var (decoded, state) = RunLive(codec, [(lostLeft, lostRight)]);

        Assert.Null(decoded);
        Assert.Equal(
            new[] { (0, true), (1, false), (2, true) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
    }

    /// <summary>
    /// FH を受けずに BH-0 から受信し（BH 単独受信）、BD-1 の後半で信号が 3 秒消えても、BLK-1 だけが NG になり BLK-2 は OK になること。
    /// </summary>
    [Fact]
    public void Live_StandaloneSignalLostDuringDataBlock_ReceivesNextBlockAfterReturn()
    {
        var codec = new FileWavCodec(Profile);
        var payload = new byte[(FileWavCodec.DataBlockBytes * 2) + 500];
        new Random(3).NextBytes(payload);
        var (left, right, events) = Encode(codec, payload);
        var (lostLeft, lostRight) = ReplaceDataBlockTailWithSilence(left, right, events, blockOrdinal: 1, silenceSeconds: 3);
        var firstBh = events.FindIndex(x => x.Kind == TransmissionFrameKind.Bh);
        var start = (int)events[firstBh - 1].End;

        var (decoded, state) = RunLive(codec, [(lostLeft[start..], lostRight[start..])]);

        Assert.Null(decoded);
        Assert.Equal(
            new[] { (0, true), (1, false), (2, true) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
    }

    /// <summary>
    /// 送信順で指定番目の BD の後半を取り除いて指定秒数の無音に置き換え、次の BH から元の波形に戻した波形を返します（元の配列は変えない）。
    /// </summary>
    /// <param name="left">L 送信波形。</param>
    /// <param name="right">R 送信波形。</param>
    /// <param name="events">送信フレームの種別と終端位置。</param>
    /// <param name="blockOrdinal">何番目（0 始まり）の BD か。</param>
    /// <param name="silenceSeconds">挟む無音の秒数。</param>
    private static (Complex[] Left, Complex[] Right) ReplaceDataBlockTailWithSilence(
        Complex[] left,
        Complex[] right,
        List<(TransmissionFrameKind Kind, long End)> events,
        int blockOrdinal,
        double silenceSeconds)
    {
        var (bdStart, bdEnd) = FindDataBlockRange(events, blockOrdinal);
        var cut = (bdStart + bdEnd) / 2;
        var silence = new Complex[(int)(SampleRate * silenceSeconds)];
        return ([.. left[..cut], .. silence, .. left[bdEnd..]], [.. right[..cut], .. silence, .. right[bdEnd..]]);
    }

    /// <summary>
    /// 送信順で指定番目の BD の中央から指定秒数のサンプルを取り除いた波形を返します（元の配列は変えない）。
    /// </summary>
    /// <param name="left">L 送信波形。</param>
    /// <param name="right">R 送信波形。</param>
    /// <param name="events">送信フレームの種別と終端位置。</param>
    /// <param name="blockOrdinal">何番目（0 始まり）の BD か。</param>
    /// <param name="removeSeconds">取り除く秒数。</param>
    private static (Complex[] Left, Complex[] Right) RemoveSamplesInDataBlock(
        Complex[] left,
        Complex[] right,
        List<(TransmissionFrameKind Kind, long End)> events,
        int blockOrdinal,
        double removeSeconds)
    {
        var (bdStart, bdEnd) = FindDataBlockRange(events, blockOrdinal);
        var cutStart = (bdStart + bdEnd) / 2;
        var cutEnd = cutStart + (int)(SampleRate * removeSeconds);
        return ([.. left[..cutStart], .. left[cutEnd..]], [.. right[..cutStart], .. right[cutEnd..]]);
    }

    /// <summary>
    /// 送信順で指定番目の BD の後ろ 3/4 を雑音に置き換えた波形を返します（元の配列は変えない）。
    /// </summary>
    /// <param name="left">L 送信波形。</param>
    /// <param name="right">R 送信波形。</param>
    /// <param name="events">送信フレームの種別と終端位置。</param>
    /// <param name="blockOrdinal">何番目（0 始まり）の BD か。</param>
    private static (Complex[] Left, Complex[] Right) DamageDataBlock(
        Complex[] left,
        Complex[] right,
        List<(TransmissionFrameKind Kind, long End)> events,
        int blockOrdinal)
    {
        var (bdStart, bdEnd) = FindDataBlockRange(events, blockOrdinal);
        var damagedLeft = (Complex[])left.Clone();
        var damagedRight = (Complex[])right.Clone();
        var rng = new Random(4);
        for (var i = bdStart + ((bdEnd - bdStart) / 4); i < bdEnd; i++)
        {
            damagedLeft[i] = new Complex((rng.NextDouble() - 0.5) * 0.2, 0);
            damagedRight[i] = new Complex((rng.NextDouble() - 0.5) * 0.2, 0);
        }

        return (damagedLeft, damagedRight);
    }

    /// <summary>
    /// 送信順で指定番目の BD のサンプル範囲 [開始, 終了) を返します。
    /// </summary>
    /// <param name="events">送信フレームの種別と終端位置。</param>
    /// <param name="blockOrdinal">何番目（0 始まり）の BD か。</param>
    private static (int Start, int End) FindDataBlockRange(List<(TransmissionFrameKind Kind, long End)> events, int blockOrdinal)
    {
        var count = 0;
        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].Kind == TransmissionFrameKind.Bd && count++ == blockOrdinal)
            {
                return ((int)events[i - 1].End, (int)events[i].End);
            }
        }

        throw new InvalidOperationException($"BD #{blockOrdinal} が見つかりません。");
    }

    /// <summary>
    /// ペイロードを送信波形へ符号化し、各フレームの終端サンプル位置も返します。
    /// </summary>
    private static (Complex[] Left, Complex[] Right, List<(TransmissionFrameKind Kind, long End)> Events) Encode(
        FileWavCodec codec,
        byte[] payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta_test_reread_{Guid.NewGuid():N}.bin");
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

    /// <summary>
    /// 複数回の再生（各回の前に 1 秒の無音）を雑音を加えてつなぎ、実時間の <see cref="FeedSpeed"/> 倍でリアルタイムセッションへ流します。
    /// </summary>
    /// <param name="codec">受信に使うコーデック。</param>
    /// <param name="plays">再生ごとの L/R 送信波形。</param>
    /// <param name="notifyInputCompleted">false なら入力終了を通知せず、流し終えて受信処理が落ち着いた時点で止める（ライブ受信の途中状態を見る）。</param>
    /// <returns>復元できたファイル（未完了なら null）と最終の進捗状態。</returns>
    private static (byte[]? Decoded, ProgressiveDecodeState State) RunLive(
        FileWavCodec codec,
        IReadOnlyList<(Complex[] Left, Complex[] Right)> plays,
        bool notifyInputCompleted = true)
    {
        var rng = new Random(2);
        var inLeft = new List<Complex>();
        var inRight = new List<Complex>();
        foreach (var (left, right) in plays)
        {
            for (var i = 0; i < SampleRate; i++)
            {
                inLeft.Add(new Complex((rng.NextDouble() - 0.5) * 0.001, 0));
                inRight.Add(new Complex((rng.NextDouble() - 0.5) * 0.001, 0));
            }

            for (var i = 0; i < left.Length; i++)
            {
                inLeft.Add(new Complex((left[i].Real * 4) + ((rng.NextDouble() - 0.5) * 0.001), 0));
                inRight.Add(new Complex((right[i].Real * 4) + ((rng.NextDouble() - 0.5) * 0.001), 0));
            }
        }

        var allLeft = inLeft.ToArray();
        var allRight = inRight.ToArray();
        using var session = new RealtimeDecodeSession(
            codec,
            SampleRate,
            ChannelMode.Stereo,
            DecodeRuntimeTuning.Default,
            pollInterval: TimeSpan.FromMilliseconds(100),
            minAttemptSeconds: 2);
        session.Start();
        var stopwatch = Stopwatch.StartNew();
        var targetSeconds = 0.0;
        byte[]? decoded = null;
        const int chunk = 2205;
        for (var p = 0; p < allLeft.Length && decoded is null; p += chunk)
        {
            var count = Math.Min(chunk, allLeft.Length - p);
            session.AppendSamples(allLeft.AsSpan(p, count), allRight.AsSpan(p, count));
            targetSeconds += count / (double)SampleRate / FeedSpeed;
            var wait = targetSeconds - stopwatch.Elapsed.TotalSeconds;
            if (wait > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(wait));
            }

            if (session.TryConsumeDecoded(out var d) && d.Length > 0)
            {
                decoded = d;
            }
        }

        if (!notifyInputCompleted)
        {
            // 流し終えたあと、残りの復号が追いつくのを待ってから止める
            Thread.Sleep(TimeSpan.FromSeconds(3));
            session.Stop();
            return (decoded, session.ProgressiveState);
        }

        session.NotifyInputCompleted();
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (decoded is null && DateTime.UtcNow < deadline && session.GetSnapshot().IsRunning)
        {
            if (session.TryConsumeDecoded(out var d) && d.Length > 0)
            {
                decoded = d;
                break;
            }

            Thread.Sleep(100);
        }

        if (decoded is null && session.TryConsumeDecoded(out var last) && last.Length > 0)
        {
            decoded = last;
        }

        return (decoded, session.ProgressiveState);
    }
}
