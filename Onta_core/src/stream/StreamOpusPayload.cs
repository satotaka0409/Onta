namespace Onta.Stream;

/// <summary>
/// 1024 バイトペイロード内に Opus フレームを長さ付きで詰めます。
/// 形式: 繰り返し [u16 BE length][bytes…]、余りは 0。
/// </summary>
public static class StreamOpusPayload
{
    /// <summary>
    /// Opus フレーム列を 1024 バイトチャンクへ分割します。
    /// </summary>
    public static List<byte[]> Pack(IReadOnlyList<byte[]> opusFrames)
    {
        var chunks = new List<byte[]>();
        var chunk = new byte[StreamConstants.PayloadBytes];
        var offset = 0;

        void Flush()
        {
            if (offset == 0)
            {
                return;
            }

            chunks.Add(chunk);
            chunk = new byte[StreamConstants.PayloadBytes];
            offset = 0;
        }

        foreach (var frame in opusFrames)
        {
            if (frame.Length == 0 || frame.Length > 65535)
            {
                continue;
            }

            if (offset + 2 + frame.Length > StreamConstants.PayloadBytes)
            {
                Flush();
            }

            if (2 + frame.Length > StreamConstants.PayloadBytes)
            {
                // 単体で大きすぎる場合は切り捨て
                continue;
            }

            chunk[offset] = (byte)(frame.Length >> 8);
            chunk[offset + 1] = (byte)frame.Length;
            Buffer.BlockCopy(frame, 0, chunk, offset + 2, frame.Length);
            offset += 2 + frame.Length;
        }

        Flush();
        if (chunks.Count == 0)
        {
            chunks.Add(new byte[StreamConstants.PayloadBytes]);
        }

        return chunks;
    }

    /// <summary>
    /// 1024 バイトから Opus フレームを取り出します。
    /// </summary>
    /// <param name="payload">ペイロード。</param>
    /// <returns>Opus フレーム列。</returns>
    public static List<byte[]> Unpack(ReadOnlySpan<byte> payload)
    {
        var frames = new List<byte[]>();
        var i = 0;
        while (i + 2 <= payload.Length)
        {
            var len = (payload[i] << 8) | payload[i + 1];
            i += 2;
            if (len <= 0)
            {
                break;
            }

            if (i + len > payload.Length)
            {
                break;
            }

            var frame = payload.Slice(i, len).ToArray();
            frames.Add(frame);
            i += len;
        }

        return frames;
    }
}

/// <summary>
/// Opus フレームを呼び出しをまたいで 1024 バイトペイロードへ詰める状態付きパッカーです。
/// 満杯になったチャンクだけを確定するため、ペイロード後半（R チャネル側）がゼロ埋めにならない。
/// </summary>
public sealed class StreamOpusPayloadPacker
{
    private byte[] _chunk = new byte[StreamConstants.PayloadBytes];
    private int _offset;

    /// <summary>
    /// Opus フレームを追加し、次フレームが入らず確定したチャンクを <paramref name="completed"/> へ追加します。
    /// </summary>
    /// <param name="frame">Opus フレーム。</param>
    /// <param name="completed">確定チャンクの追加先。</param>
    public void Add(byte[] frame, ICollection<byte[]> completed)
    {
        if (frame.Length == 0 || 2 + frame.Length > StreamConstants.PayloadBytes)
        {
            return;
        }

        if (_offset + 2 + frame.Length > StreamConstants.PayloadBytes)
        {
            completed.Add(_chunk);
            _chunk = new byte[StreamConstants.PayloadBytes];
            _offset = 0;
        }

        _chunk[_offset] = (byte)(frame.Length >> 8);
        _chunk[_offset + 1] = (byte)frame.Length;
        Buffer.BlockCopy(frame, 0, _chunk, _offset + 2, frame.Length);
        _offset += 2 + frame.Length;
    }

    /// <summary>
    /// 詰めかけのチャンクを確定して返します（ストリーム終端用）。
    /// </summary>
    /// <returns>未送出データがあればチャンク、なければ null。</returns>
    public byte[]? FlushPartial()
    {
        if (_offset == 0)
        {
            return null;
        }

        var chunk = _chunk;
        _chunk = new byte[StreamConstants.PayloadBytes];
        _offset = 0;
        return chunk;
    }
}
