using System.Text;

namespace Onta.Stream;

/// <summary>
/// 受信側で StreamId 単位に曲情報を組み立てるアセンブラです。
/// </summary>
public sealed class StreamMetaAssembler
{
    private readonly Dictionary<byte, byte[]> _title = new();
    private readonly Dictionary<byte, byte[]> _artist = new();
    private readonly Dictionary<byte, byte[]> _cover = new();
    private ushort? _streamId;
    private byte _titleTotal = 1;
    private byte _artistTotal = 1;
    private byte _coverTotal = 1;

    /// <summary>現在のストリーム ID。未確定時は null。</summary>
    public ushort? CurrentStreamId => _streamId;

    /// <summary>曲タイトルが揃ったか。</summary>
    public bool TitleComplete => _title.Count >= _titleTotal && _titleTotal > 0;

    /// <summary>アーティストが揃ったか。</summary>
    public bool ArtistComplete => _artist.Count >= _artistTotal && _artistTotal > 0;

    /// <summary>ジャケ写が揃ったか（途中でも部分表示可）。</summary>
    public bool CoverComplete => _cover.Count >= _coverTotal && _coverTotal > 0;

    /// <summary>
    /// メタスロットを取り込みます。StreamId が変われば全リセットします。
    /// </summary>
    /// <param name="streamId">ストリーム ID。</param>
    /// <param name="kind">種別。</param>
    /// <param name="totalBlocks">総ブロック数。</param>
    /// <param name="blockIndex">ブロック位置。</param>
    /// <param name="data">16 バイトデータ。</param>
    /// <returns>StreamId が切り替わったとき true。</returns>
    public bool Ingest(ushort streamId, StreamMetaKind kind, byte totalBlocks, byte blockIndex, ReadOnlySpan<byte> data)
    {
        var reset = false;
        if (_streamId is null || _streamId.Value != streamId)
        {
            Reset();
            _streamId = streamId;
            reset = true;
        }

        var map = kind switch
        {
            StreamMetaKind.Title => _title,
            StreamMetaKind.Artist => _artist,
            StreamMetaKind.Cover => _cover,
            _ => null,
        };
        if (map is null)
        {
            return reset;
        }

        switch (kind)
        {
            case StreamMetaKind.Title:
                _titleTotal = Math.Max((byte)1, totalBlocks);
                break;
            case StreamMetaKind.Artist:
                _artistTotal = Math.Max((byte)1, totalBlocks);
                break;
            case StreamMetaKind.Cover:
                _coverTotal = Math.Max((byte)1, totalBlocks);
                break;
        }

        var copy = new byte[StreamConstants.MetaBlockDataBytes];
        data[..Math.Min(data.Length, copy.Length)].CopyTo(copy);
        map[blockIndex] = copy;
        return reset;
    }

    /// <summary>
    /// 蓄積をすべて破棄します。
    /// </summary>
    public void Reset()
    {
        _streamId = null;
        _title.Clear();
        _artist.Clear();
        _cover.Clear();
        _titleTotal = 1;
        _artistTotal = 1;
        _coverTotal = 1;
    }

    /// <summary>
    /// 取得済みタイトル文字列を返します（途中でも連結）。
    /// </summary>
    /// <returns>途中まででも連結した UTF-8 文字列。未取得なら空文字。</returns>
    public string GetTitleText() => DecodeUtf8(_title, _titleTotal);

    /// <summary>
    /// 取得済みアーティスト文字列を返します。
    /// </summary>
    /// <returns>途中まででも連結した UTF-8 文字列。未取得なら空文字。</returns>
    public string GetArtistText() => DecodeUtf8(_artist, _artistTotal);

    /// <summary>
    /// 取得済みジャケ写バイトを返します（欠損ブロックは 0 埋め）。
    /// </summary>
    /// <returns>ブロック順に連結したジャケ写。完全受信時は末尾の 0 を落とします。</returns>
    public byte[] GetCoverBytes()
    {
        var total = Math.Max(1, (int)_coverTotal);
        var result = new byte[total * StreamConstants.MetaBlockDataBytes];
        for (var i = 0; i < total; i++)
        {
            if (_cover.TryGetValue((byte)i, out var block))
            {
                Buffer.BlockCopy(block, 0, result, i * StreamConstants.MetaBlockDataBytes, StreamConstants.MetaBlockDataBytes);
            }
        }

        // 末尾の 0 パディングを可能な範囲で落とす（完全受信時）
        if (CoverComplete)
        {
            var end = result.Length;
            while (end > 0 && result[end - 1] == 0)
            {
                end--;
            }

            if (end < result.Length)
            {
                Array.Resize(ref result, end);
            }
        }

        return result;
    }

    /// <summary>
    /// ブロック辞書を位置順に連結し、末尾の 0 を除いて UTF-8 文字列へ変換します。
    /// </summary>
    /// <param name="map">ブロック位置から 16 バイトデータへの対応。</param>
    /// <param name="total">期待する総ブロック数。欠損位置は飛ばします。</param>
    /// <returns>連結した文字列。空なら空文字。</returns>
    private static string DecodeUtf8(Dictionary<byte, byte[]> map, byte total)
    {
        if (map.Count == 0)
        {
            return string.Empty;
        }

        var buf = new List<byte>(map.Count * StreamConstants.MetaBlockDataBytes);
        var max = Math.Max(1, (int)total);
        for (var i = 0; i < max; i++)
        {
            if (!map.TryGetValue((byte)i, out var block))
            {
                continue;
            }

            var len = block.Length;
            while (len > 0 && block[len - 1] == 0)
            {
                len--;
            }

            for (var j = 0; j < len; j++)
            {
                buf.Add(block[j]);
            }
        }

        return Encoding.UTF8.GetString(buf.ToArray());
    }
}
