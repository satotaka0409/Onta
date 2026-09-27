using System.Text;

namespace Onta.Stream;

/// <summary>
/// 送信側で曲タイトル／アーティスト／ジャケ写をローテーションするメタ供給器です。
/// データが無い種別は出力しません。
/// </summary>
public sealed class StreamMetaRotator
{
    private readonly List<Slot> _slots = new();
    private int _phase;

    /// <summary>
    /// UTF-8 テキストとジャケ写バイナリからローテータを構築します。
    /// </summary>
    /// <param name="title">曲タイトル。</param>
    /// <param name="artist">アーティスト。</param>
    /// <param name="coverBytes">ジャケ写（空可）。</param>
    public StreamMetaRotator(string title, string artist, byte[]? coverBytes)
    {
        TryAdd(StreamMetaKind.Title, Encoding.UTF8.GetBytes(title ?? string.Empty));
        TryAdd(StreamMetaKind.Artist, Encoding.UTF8.GetBytes(artist ?? string.Empty));
        TryAdd(StreamMetaKind.Cover, coverBytes ?? Array.Empty<byte>());
    }

    /// <summary>
    /// 次のメタスロットを取り出します（存在する種別だけ Title→Artist→Cover の順で回す）。
    /// </summary>
    /// <returns>種別・総ブロック数・位置・16 バイトデータ。</returns>
    public (StreamMetaKind Kind, byte TotalBlocks, byte BlockIndex, byte[] Data) Next()
    {
        // パケットには必ずメタ欄が必要なので、全種別が空のときだけ Title の空ブロックを出す。
        if (_slots.Count == 0)
        {
            return (StreamMetaKind.Title, 1, 0, new byte[StreamConstants.MetaBlockDataBytes]);
        }

        var slot = _slots[_phase++ % _slots.Count];
        var total = (byte)Math.Clamp(slot.Blocks.Length, 1, StreamConstants.MetaMaxBlocks);
        var blockIndex = (byte)(slot.Index % slot.Blocks.Length);
        slot.Index++;
        var data = new byte[StreamConstants.MetaBlockDataBytes];
        Buffer.BlockCopy(slot.Blocks[blockIndex], 0, data, 0, slot.Blocks[blockIndex].Length);
        return (slot.Kind, total, blockIndex, data);
    }

    /// <summary>
    /// 非空データだけスロットに登録します。
    /// </summary>
    private void TryAdd(StreamMetaKind kind, byte[] raw)
    {
        var blocks = SplitToBlocks(raw);
        if (blocks.Length == 0)
        {
            return;
        }

        _slots.Add(new Slot(kind, blocks));
    }

    /// <summary>
    /// 生バイト列を 16 バイト単位ブロックへ分割します。
    /// </summary>
    private static byte[][] SplitToBlocks(byte[] raw)
    {
        if (raw.Length == 0)
        {
            return Array.Empty<byte[]>();
        }

        var count = Math.Min(
            StreamConstants.MetaMaxBlocks,
            (raw.Length + StreamConstants.MetaBlockDataBytes - 1) / StreamConstants.MetaBlockDataBytes);
        var blocks = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            var offset = i * StreamConstants.MetaBlockDataBytes;
            var len = Math.Min(StreamConstants.MetaBlockDataBytes, raw.Length - offset);
            var block = new byte[StreamConstants.MetaBlockDataBytes];
            Buffer.BlockCopy(raw, offset, block, 0, len);
            blocks[i] = block;
        }

        return blocks;
    }

    /// <summary>1 種別分のブロック列と送信位置です。</summary>
    private sealed class Slot
    {
        public Slot(StreamMetaKind kind, byte[][] blocks)
        {
            Kind = kind;
            Blocks = blocks;
        }

        public StreamMetaKind Kind { get; }

        public byte[][] Blocks { get; }

        public int Index { get; set; }
    }
}
