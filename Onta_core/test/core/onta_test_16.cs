using System.Diagnostics;
using System.Numerics;
using Onta.Core;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// 16 ブロックを超えるファイル（途中 FH あり）をリアルタイム受信で復号できることを確認します。
/// 1. 先頭から受信すると途中 FH 直後のブロックも含めて全ブロックを復元できる
/// 2. 途中 FH から受信すると、そこから後ろのブロックだけを OK にし、未受信ブロックを NG にしない
/// 3. FH 復号中に入力が大量に溜まっても先頭 FH を捨てずに全ブロックを復元できる
/// 4. BH から受信を始めても FH を待たずにブロックを受け、途中 FH の確定時に取り込む
/// 5. FH が来ないまま BH から受けたブロックは不明ブロックとして残り、既知ファイルなら画面用の進捗を出す
/// </summary>
[Collection(RealtimeTestCollection.Name)]
public sealed class OntaTest16
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
    /// 1. 先頭から受信し、途中 FH 直後の BLK-16 を含む全ブロックを復元できること。
    /// </summary>
    [Fact]
    public void Live_FromStart_RestoresBlockAfterMidFileHeader()
    {
        var codec = new FileWavCodec(Profile);
        var payload = BuildPayload();
        var (left, right, _) = Encode(codec, payload);

        var (decoded, state, _) = RunLive(codec, left, right, start: 0);

        Assert.Equal(18, state.BlockCount);
        Assert.Equal(18, state.AcceptedBlockCount);
        Assert.Equal(payload, decoded);
    }

    /// <summary>
    /// 3. アンカー検出後、先頭 FH の確定前に 60 秒分が一度に届いても（20 秒を超えても）先頭 FH から全ブロックを復元できること。
    /// </summary>
    [Fact]
    public void Live_BurstBeforeHeaderConfirmed_KeepsFirstFileHeader()
    {
        var codec = new FileWavCodec(Profile);
        var payload = BuildPayload();
        var (left, right, _) = Encode(codec, payload);

        var (decoded, state, _) = RunLive(codec, left, right, start: 0, burstFromSeconds: 6, burstSeconds: 60);

        Assert.Equal(18, state.AcceptedBlockCount);
        Assert.Equal(payload, decoded);
    }

    /// <summary>
    /// 2. 途中 FH から受信し、BLK-16/17 だけが OK になり、未受信の BLK-0〜15 は NG にならないこと。
    /// </summary>
    [Fact]
    public void Live_FromMidFileHeader_MarksOnlyReceivedBlocks()
    {
        var codec = new FileWavCodec(Profile);
        var payload = BuildPayload();
        var (left, right, events) = Encode(codec, payload);
        var start = FindSecondFileHeaderStart(events);

        var (decoded, state, stopped) = RunLive(codec, left, right, start);

        Assert.True(stopped, "入力終了後もセッションが止まらない");
        Assert.Null(decoded);
        Assert.Equal(18, state.BlockCount);
        Assert.Equal(2, state.AcceptedBlockCount);
        Assert.Equal(
            new[] { (16, true), (17, true) },
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
    }

    /// <summary>
    /// 4. FH を待たずに BLK-4 の BH から受信し、BLK-4〜15 を FH 無しで受け、途中 FH 確定時に取り込んで BLK-4〜17 を OK にすること。
    /// 既知ファイルの照会は BH のファイルハッシュで呼ばれること。
    /// </summary>
    [Fact]
    public void Live_FromBlockHeader_ReceivesBeforeFileHeader()
    {
        var codec = new FileWavCodec(Profile);
        var payload = BuildPayload();
        var (left, right, events) = Encode(codec, payload);
        var start = FindBlockHeaderStart(events, blockOrdinal: 4);
        var resolvedHashes = new List<string>();

        var (decoded, state, stopped) = RunLive(
            codec,
            left,
            right,
            start,
            configure: s => s.ResolveKnownFile = hash =>
            {
                resolvedHashes.Add(hash);
                return new KnownReceiveFile("known.bin", payload.Length, 18);
            });

        Assert.True(stopped, "入力終了後もセッションが止まらない");
        Assert.Null(decoded);
        Assert.True(state.HeaderReady, "途中 FH が確定していない");
        Assert.Equal(
            Enumerable.Range(4, 14).Select(i => (i, true)).ToArray(),
            state.BlockBdOutcomeByIndex.OrderBy(x => x.Key).Select(x => (x.Key, x.Value)).ToArray());
        Assert.Equal(14, state.AcceptedBlockCount);
        Assert.Empty(state.OrphanPayloadByHash);
        var fileHash = Convert.ToHexString(Hash.ComputeSha256(payload));
        Assert.Equal(new[] { fileHash }, resolvedHashes.Distinct().ToArray());
    }

    /// <summary>
    /// 5. 途中 FH より後ろの BLK-17 の BH から受信すると、FH が来なくても BLK-17 を不明ブロックとして受け、画面用にファイル情報と BLK-17 の進捗を出すこと。
    /// </summary>
    [Fact]
    public void Live_FromLastBlockHeader_RegistersUnknownBlockAndPublishesKnownFile()
    {
        var codec = new FileWavCodec(Profile);
        var payload = BuildPayload();
        var (left, right, events) = Encode(codec, payload);
        var start = FindBlockHeaderStart(events, blockOrdinal: 17);
        var lastFileHeader = events.FindLastIndex(x => x.Kind == TransmissionFrameKind.Fh);
        var end = (int)events[lastFileHeader - 1].End;

        var (decoded, state, _) = RunLive(
            codec,
            left,
            right,
            start,
            configure: s => s.ResolveKnownFile = _ => new KnownReceiveFile("known.bin", payload.Length, 18),
            stopBeforeTail: true,
            end: end);

        Assert.Null(decoded);
        Assert.False(state.HeaderReady);
        Assert.Equal(new[] { (17, true) }, state.BlockBdOutcomeByIndex.Select(x => (x.Key, x.Value)).ToArray());
        var orphan = Assert.Single(state.OrphanPayloadByHash);
        Assert.StartsWith("17:", orphan.Key, StringComparison.Ordinal);
        Assert.Equal(payload.AsSpan(FileWavCodec.DataBlockBytes * 17).ToArray(), orphan.Value);

        var status = state.ReadExecutionStatus();
        Assert.False(status.IsAnalyzing);
        Assert.Equal("known.bin", status.FileName);
        Assert.Equal(18, status.Progress.TotalBlockCount);
        Assert.Equal(17, status.Progress.CurrentBlockIndex);
    }

    /// <summary>
    /// 指定した送信順のブロックの BH 手前（直前フレームの終端）のサンプル位置を返します。
    /// </summary>
    /// <param name="events">送信フレームの種別と終端位置。</param>
    /// <param name="blockOrdinal">何番目（0 始まり）の BH か。</param>
    private static int FindBlockHeaderStart(List<(TransmissionFrameKind Kind, long End)> events, int blockOrdinal)
    {
        var count = 0;
        for (var i = 1; i < events.Count; i++)
        {
            if (events[i].Kind == TransmissionFrameKind.Bh && count++ == blockOrdinal)
            {
                return (int)events[i - 1].End;
            }
        }

        throw new InvalidOperationException($"BH #{blockOrdinal} が見つかりません。");
    }

    /// <summary>
    /// 17 ブロック＋端数（計 18 ブロック）の乱数ペイロードを作ります。
    /// </summary>
    private static byte[] BuildPayload()
    {
        var payload = new byte[(FileWavCodec.DataBlockBytes * 17) + 1000];
        new Random(1).NextBytes(payload);
        return payload;
    }

    /// <summary>
    /// ペイロードを送信波形へ符号化し、各フレームの終端サンプル位置も返します。
    /// </summary>
    private static (Complex[] Left, Complex[] Right, List<(TransmissionFrameKind Kind, long End)> Events) Encode(
        FileWavCodec codec,
        byte[] payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta_test16_{Guid.NewGuid():N}.bin");
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
    /// 2 回目の FH（途中 FH）の手前、直前フレームの終端サンプル位置を返します。
    /// </summary>
    private static int FindSecondFileHeaderStart(List<(TransmissionFrameKind Kind, long End)> events)
    {
        var fileHeaderCount = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Kind == TransmissionFrameKind.Fh && ++fileHeaderCount == 2)
            {
                return (int)events[i - 1].End;
            }
        }

        throw new InvalidOperationException("途中 FH が見つかりません。");
    }

    /// <summary>
    /// 先頭 1 秒の無音と雑音を加えた波形を、実時間の <see cref="FeedSpeed"/> 倍でリアルタイムセッションへ流します。
    /// </summary>
    /// <param name="start">送信波形のうち受信を始めるサンプル位置。</param>
    /// <param name="burstFromSeconds">一度に流し込みを始める入力上の秒数。</param>
    /// <param name="burstSeconds">待たずに一度に流し込む秒数（受信処理の遅れの再現用）。0 なら常に一定速度。</param>
    /// <param name="configure">開始前に段階デコード状態へ設定を加える処理（既知ファイル照会など）。</param>
    /// <param name="stopBeforeTail">true なら入力終了を通知せず、流し終えて受信処理が落ち着いた時点で返す（ライブ受信の途中状態を見る）。</param>
    /// <param name="end">送信波形のうち受信を終えるサンプル位置（負なら末尾まで）。指定時は後ろに 1 秒の雑音を足す。</param>
    /// <returns>復元できたファイル（未完了なら null）、最終の進捗状態、セッションが停止したか。</returns>
    private static (byte[]? Decoded, ProgressiveDecodeState State, bool Stopped) RunLive(
        FileWavCodec codec,
        Complex[] left,
        Complex[] right,
        int start,
        int burstFromSeconds = 0,
        int burstSeconds = 0,
        Action<ProgressiveDecodeState>? configure = null,
        bool stopBeforeTail = false,
        int end = -1)
    {
        var rng = new Random(2);
        var lead = SampleRate;
        var srcEnd = end < 0 ? left.Length : Math.Min(end, left.Length);
        var total = lead + srcEnd - start + (end < 0 ? 0 : SampleRate);
        var inLeft = new Complex[total];
        var inRight = new Complex[total];
        for (var i = 0; i < total; i++)
        {
            var src = i - lead + start;
            var l = src >= start && src < srcEnd ? left[src].Real * 4 : 0.0;
            var r = src >= start && src < srcEnd ? right[src].Real * 4 : 0.0;
            inLeft[i] = new Complex(l + ((rng.NextDouble() - 0.5) * 0.001), 0);
            inRight[i] = new Complex(r + ((rng.NextDouble() - 0.5) * 0.001), 0);
        }

        using var session = new RealtimeDecodeSession(
            codec,
            SampleRate,
            ChannelMode.Stereo,
            DecodeRuntimeTuning.Default,
            pollInterval: TimeSpan.FromMilliseconds(100),
            minAttemptSeconds: 2);
        configure?.Invoke(session.ProgressiveState);
        session.Start();
        var stopwatch = Stopwatch.StartNew();
        var burstBegin = burstFromSeconds * SampleRate;
        var burstEnd = burstBegin + (burstSeconds * SampleRate);
        var targetSeconds = 0.0;
        byte[]? decoded = null;
        const int chunk = 2205;
        for (var p = 0; p < total && decoded is null; p += chunk)
        {
            var count = Math.Min(chunk, total - p);
            session.AppendSamples(inLeft.AsSpan(p, count), inRight.AsSpan(p, count));
            if (p >= burstBegin && p < burstEnd)
            {
                continue;
            }

            // 一度に流し込む前は実時間で送り、アンカー検出を受信処理の負荷に左右されないようにする
            var speed = burstSeconds > 0 && p < burstBegin ? 1.0 : FeedSpeed;
            targetSeconds += count / (double)SampleRate / speed;
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

        if (stopBeforeTail)
        {
            // 末尾は無音と雑音のみ。BD の復号を待ってから止める
            var settle = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < settle && session.ProgressiveState.OrphanPayloadByHash.Count == 0)
            {
                Thread.Sleep(100);
            }

            Thread.Sleep(500);
            session.Stop();
            return (decoded, session.ProgressiveState, true);
        }

        session.NotifyInputCompleted();
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (decoded is null && DateTime.UtcNow < deadline)
        {
            if (session.TryConsumeDecoded(out var d) && d.Length > 0)
            {
                decoded = d;
                break;
            }

            if (!session.GetSnapshot().IsRunning)
            {
                break;
            }

            Thread.Sleep(100);
        }

        if (decoded is null && session.TryConsumeDecoded(out var last) && last.Length > 0)
        {
            decoded = last;
        }
        return (decoded, session.ProgressiveState, !session.GetSnapshot().IsRunning);
    }
}

/// <summary>
/// 実時間で入力を流すテストを他のテストと並行させないためのコレクションです（CPU を取り合うと受信処理が遅れ、無信号扱いで入力が捨てられる）。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealtimeTestCollection
{
    /// <summary>コレクション名。</summary>
    public const string Name = "Realtime";
}
