namespace Onta.Stream;

/// <summary>
/// 曲情報の種別です。
/// </summary>
public enum StreamMetaKind : byte
{
    /// <summary>曲タイトル。</summary>
    Title = 0x00,

    /// <summary>アーティスト。</summary>
    Artist = 0x01,

    /// <summary>ジャケ写。</summary>
    Cover = 0x02,
}

/// <summary>
/// ストリームパケット 1 個分の論理表現です。
/// </summary>
public sealed class StreamPacket
{
    /// <summary>ストリーム速度 ID。</summary>
    public StreamModeId ModeId { get; init; }

    /// <summary>ストリーム識別子（同一ストリーム判定用）。</summary>
    public ushort StreamId { get; init; }

    /// <summary>曲情報種別。</summary>
    public StreamMetaKind MetaKind { get; init; }

    /// <summary>当該種別の総ブロック数。</summary>
    public byte MetaTotalBlocks { get; init; }

    /// <summary>曲情報ブロック位置（0 始まり）。</summary>
    public byte MetaBlockIndex { get; init; }

    /// <summary>曲情報データ（最大 16 バイト。短い場合は 0 パディング）。</summary>
    public byte[] MetaData { get; init; } = new byte[StreamConstants.MetaBlockDataBytes];

    /// <summary>ストリームペイロード（1024 バイト）。</summary>
    public byte[] Payload { get; init; } = new byte[StreamConstants.PayloadBytes];

    /// <summary>
    /// 1051 バイトのワイヤー形式へパックします。
    /// </summary>
    /// <returns>パケットバイト列。</returns>
    public byte[] Pack()
    {
        var packet = new byte[StreamConstants.PacketBytes];
        WriteHeader(packet.AsSpan(0, StreamConstants.HeaderBytes));
        WriteMeta(packet.AsSpan(StreamConstants.HeaderBytes, StreamConstants.MetaBytes));
        Buffer.BlockCopy(Payload, 0, packet, StreamConstants.HeaderBytes + StreamConstants.MetaBytes, StreamConstants.PayloadBytes);
        return packet;
    }

    /// <summary>
    /// ワイヤー形式からパケットを復元します（メタ CRC 検証付き）。
    /// </summary>
    /// <param name="packet">1051 バイト。</param>
    /// <param name="result">成功時のパケット。</param>
    /// <returns>パイロット・モード・メタ CRC が妥当なら true。</returns>
    public static bool TryUnpack(ReadOnlySpan<byte> packet, out StreamPacket? result)
    {
        result = null;
        if (packet.Length < StreamConstants.PacketBytes)
        {
            return false;
        }

        if (packet[0] != StreamConstants.HeaderPilot[0]
            || packet[1] != StreamConstants.HeaderPilot[1]
            || packet[2] != StreamConstants.HeaderPilot[2])
        {
            return false;
        }

        var mode = StreamMode.TryResolve(packet[3]);
        if (mode is null)
        {
            return false;
        }

        var meta = packet.Slice(StreamConstants.HeaderBytes, StreamConstants.MetaBytes);
        if (!Crc16.Matches(meta.Slice(0, StreamConstants.MetaBytes - 2), meta.Slice(StreamConstants.MetaBytes - 2, 2)))
        {
            return false;
        }

        var kind = (StreamMetaKind)meta[0];
        if (kind is not StreamMetaKind.Title and not StreamMetaKind.Artist and not StreamMetaKind.Cover)
        {
            return false;
        }

        var payload = new byte[StreamConstants.PayloadBytes];
        packet.Slice(StreamConstants.HeaderBytes + StreamConstants.MetaBytes, StreamConstants.PayloadBytes).CopyTo(payload);

        var metaData = new byte[StreamConstants.MetaBlockDataBytes];
        meta.Slice(3, StreamConstants.MetaBlockDataBytes).CopyTo(metaData);

        result = new StreamPacket
        {
            ModeId = mode.Value.Id,
            StreamId = (ushort)((packet[4] << 8) | packet[5]),
            MetaKind = kind,
            MetaTotalBlocks = meta[1],
            MetaBlockIndex = meta[2],
            MetaData = metaData,
            Payload = payload,
        };
        return true;
    }

    /// <summary>
    /// ヘッダーエリア 6 バイト（パイロット・速度 ID・ストリーム ID）を書き込みます。
    /// </summary>
    /// <param name="header">書き込み先（6 バイト）。</param>
    private void WriteHeader(Span<byte> header)
    {
        header[0] = StreamConstants.HeaderPilot[0];
        header[1] = StreamConstants.HeaderPilot[1];
        header[2] = StreamConstants.HeaderPilot[2];
        header[3] = (byte)((byte)ModeId & 0x0F);
        header[4] = (byte)(StreamId >> 8);
        header[5] = (byte)StreamId;
    }

    /// <summary>
    /// 曲情報エリア 21 バイトを書き、末尾に CRC-16 を付けます。
    /// </summary>
    /// <param name="meta">書き込み先（21 バイト）。</param>
    private void WriteMeta(Span<byte> meta)
    {
        meta.Clear();
        meta[0] = (byte)MetaKind;
        meta[1] = MetaTotalBlocks;
        meta[2] = MetaBlockIndex;
        var copy = Math.Min(MetaData.Length, StreamConstants.MetaBlockDataBytes);
        MetaData.AsSpan(0, copy).CopyTo(meta.Slice(3, copy));
        var crc = Crc16.Compute(meta[..(StreamConstants.MetaBytes - 2)]);
        Crc16.WriteBigEndian(meta.Slice(StreamConstants.MetaBytes - 2, 2), crc);
    }
}
