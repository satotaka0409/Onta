using System.Text;

namespace Onta.Stream;

/// <summary>
/// 送信側で曲タイトル／アーティスト／ジャケ写をローテーションするメタ供給器です。
/// 1 周はタイトル 1 → アーティスト 1 → ジャケ写 <see cref="CoverRepeat"/> ブロックで、データが無い種別は出力しません。
/// </summary>
public sealed class StreamMetaRotator
{
    /// <summary>1 周でジャケ写を続けて送るブロック数。ジャケ写はタイトル・アーティストよりずっと大きく、等分では曲の間に送り切れないため。</summary>
    public const int CoverRepeat = 8;

    private readonly List<Slot> _schedule = new();
    private int _phase;

    /// <summary>
    /// UTF-8 テキストとジャケ写バイナリからローテータを構築します。
    /// </summary>
    /// <param name="title">曲タイトル。</param>
    /// <param name="artist">アーティスト。</param>
    /// <param name="coverBytes">ジャケ写（空可）。</param>
    public StreamMetaRotator(string title, string artist, byte[]? coverBytes)
    {
        TryAdd(StreamMetaKind.Title, Encoding.UTF8.GetBytes(title ?? string.Empty), 1);
        TryAdd(StreamMetaKind.Artist, Encoding.UTF8.GetBytes(artist ?? string.Empty), 1);
        TryAdd(StreamMetaKind.Cover, coverBytes ?? Array.Empty<byte>(), CoverRepeat);
    }

    /// <summary>
    /// 次のメタスロットを取り出します（存在する種別だけ Title→Artist→Cover×<see cref="CoverRepeat"/> の順で回す）。
    /// </summary>
    /// <returns>種別・総ブロック数・位置・16 バイトデータ。</returns>
    public (StreamMetaKind Kind, byte TotalBlocks, byte BlockIndex, byte[] Data) Next()
    {
        // パケットには必ずメタ欄が必要なので、全種別が空のときだけ Title の空ブロックを出す。
        if (_schedule.Count == 0)
        {
            return (StreamMetaKind.Title, 1, 0, new byte[StreamConstants.MetaBlockDataBytes]);
        }

        var slot = _schedule[_phase++ % _schedule.Count];
        var total = (byte)Math.Clamp(slot.Blocks.Length, 1, StreamConstants.MetaMaxBlocks);
        var blockIndex = (byte)(slot.Index % slot.Blocks.Length);
        slot.Index++;
        var data = new byte[StreamConstants.MetaBlockDataBytes];
        Buffer.BlockCopy(slot.Blocks[blockIndex], 0, data, 0, slot.Blocks[blockIndex].Length);
        return (slot.Kind, total, blockIndex, data);
    }

    /// <summary>
    /// 非空データだけ、1 周で続けて送る回数分スケジュールに登録します。
    /// </summary>
    /// <param name="kind">曲情報の種別。</param>
    /// <param name="raw">生バイト列。空なら登録しません。</param>
    /// <param name="repeat">1 周で続けて送るブロック数。</param>
    private void TryAdd(StreamMetaKind kind, byte[] raw, int repeat)
    {
        var blocks = SplitToBlocks(raw);
        if (blocks.Length == 0)
        {
            return;
        }

        var slot = new Slot(kind, blocks);
        for (var i = 0; i < repeat; i++)
        {
            _schedule.Add(slot);
        }
    }

    /// <summary>
    /// 生バイト列を 16 バイト単位ブロックへ分割します。
    /// </summary>
    /// <param name="raw">分割する生バイト列。</param>
    /// <returns>16 バイト単位（不足分は 0）のブロック列。空入力は空配列。最大 256 ブロック。</returns>
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
        /// <summary>
        /// 種別とブロック列を保持するスロットを構築します。
        /// </summary>
        /// <param name="kind">曲情報の種別。</param>
        /// <param name="blocks">16 バイト単位のブロック列。</param>
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
