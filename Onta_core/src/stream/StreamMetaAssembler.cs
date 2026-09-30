using System.Buffers.Binary;
using System.Text;
using Onta.Core;

namespace Onta.Stream;

/// <summary>
/// 受信側で StreamId 単位に曲情報を組み立てるアセンブラです。
/// </summary>
/// <remarks>
/// 全ブロックがそろい、中身の検証（テキストは UTF-8、ジャケ写は PNG のチャンク CRC）に通った種別だけを公開します。
/// 検証に失敗した種別は破棄し、次のローテーションで取り直します。
/// </remarks>
public sealed class StreamMetaAssembler
{
    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly MetaSlot _title = new();
    private readonly MetaSlot _artist = new();
    private readonly MetaSlot _cover = new();
    private ushort? _streamId;
    private ushort? _pendingStreamId;

    /// <summary>現在のストリーム ID。未確定時は null。</summary>
    public ushort? CurrentStreamId => _streamId;

    /// <summary>曲タイトルがそろい、検証に通ったか。</summary>
    public bool TitleComplete => _title.Accepted;

    /// <summary>アーティストがそろい、検証に通ったか。</summary>
    public bool ArtistComplete => _artist.Accepted;

    /// <summary>ジャケ写がそろい、検証に通ったか。</summary>
    public bool CoverComplete => _cover.Accepted;

    /// <summary>取得済みのジャケ写ブロック数。</summary>
    public int CoverReceivedBlocks => _cover.Blocks.Count;

    /// <summary>ジャケ写の総ブロック数。ジャケ写ブロックを 1 つも受信していなければ 0。</summary>
    public int CoverTotalBlocks => _cover.Seen ? _cover.Total : 0;

    /// <summary>そろったものの検証に失敗して破棄した回数（全種別の合計）。</summary>
    public int DiscardedCount { get; private set; }

    /// <summary>
    /// メタスロットを取り込みます。StreamId が変われば全リセットします。
    /// </summary>
    /// <remarks>ストリーム ID はメタ CRC の対象外のため、同じ新 ID が 2 パケット続くまで切り替えず、その間のメタは捨てます。</remarks>
    /// <param name="streamId">ストリーム ID。</param>
    /// <param name="kind">種別。</param>
    /// <param name="totalBlocks">総ブロック数。0 は 256 ブロック（1 バイトに収まらない最大値）として扱います。</param>
    /// <param name="blockIndex">ブロック位置。</param>
    /// <param name="data">16 バイトデータ。</param>
    /// <returns>StreamId が切り替わったとき true。</returns>
    public bool Ingest(ushort streamId, StreamMetaKind kind, byte totalBlocks, byte blockIndex, ReadOnlySpan<byte> data)
    {
        if (!TryAcceptStreamId(streamId, out var reset))
        {
            return false;
        }

        var slot = kind switch
        {
            StreamMetaKind.Title => _title,
            StreamMetaKind.Artist => _artist,
            StreamMetaKind.Cover => _cover,
            _ => null,
        };
        if (slot is null || slot.Accepted)
        {
            return reset;
        }

        var total = totalBlocks == 0 ? StreamConstants.MetaMaxBlocks : totalBlocks;
        if (blockIndex >= total)
        {
            return reset;
        }

        // 同一ストリーム内で総ブロック数が変わるのは異常なので、それまでの蓄積を捨てて新しい値に合わせる
        if (slot.Seen && slot.Total != total)
        {
            slot.Blocks.Clear();
        }

        slot.Seen = true;
        slot.Total = total;
        var copy = new byte[StreamConstants.MetaBlockDataBytes];
        data[..Math.Min(data.Length, copy.Length)].CopyTo(copy);
        slot.Blocks[blockIndex] = copy;

        if (slot.Blocks.Count >= slot.Total)
        {
            Validate(kind, slot);
        }

        return reset;
    }

    /// <summary>
    /// 蓄積をすべて破棄します。
    /// </summary>
    public void Reset()
    {
        _streamId = null;
        _pendingStreamId = null;
        _title.Clear();
        _artist.Clear();
        _cover.Clear();
    }

    /// <summary>
    /// 検証済みのタイトル文字列を返します。
    /// </summary>
    /// <returns>そろって検証に通った文字列。未完了なら空文字。</returns>
    public string GetTitleText() => _title.Text;

    /// <summary>
    /// 検証済みのアーティスト文字列を返します。
    /// </summary>
    /// <returns>そろって検証に通った文字列。未完了なら空文字。</returns>
    public string GetArtistText() => _artist.Text;

    /// <summary>
    /// 検証済みのジャケ写（PNG）を返します。
    /// </summary>
    /// <returns>IEND までの PNG バイト列。未完了なら空配列。</returns>
    public byte[] GetCoverBytes() => _cover.Bytes;

    /// <summary>
    /// 受信パケットのストリーム ID を現在の ID と照合し、必要なら切り替えます。
    /// </summary>
    /// <param name="streamId">受信パケットのストリーム ID。</param>
    /// <param name="reset">ID を切り替えて蓄積を捨てたとき true。</param>
    /// <returns>このパケットのメタを取り込んでよいとき true。</returns>
    private bool TryAcceptStreamId(ushort streamId, out bool reset)
    {
        reset = false;
        if (_streamId == streamId)
        {
            _pendingStreamId = null;
            return true;
        }

        if (_streamId is not null && _pendingStreamId != streamId)
        {
            _pendingStreamId = streamId;
            return false;
        }

        Reset();
        _streamId = streamId;
        reset = true;
        return true;
    }

    /// <summary>
    /// そろった種別を連結・検証し、通れば公開値を確定、失敗なら蓄積を捨てます。
    /// </summary>
    /// <param name="kind">種別。</param>
    /// <param name="slot">対象スロット。</param>
    private void Validate(StreamMetaKind kind, MetaSlot slot)
    {
        var raw = new byte[slot.Total * StreamConstants.MetaBlockDataBytes];
        for (var i = 0; i < slot.Total; i++)
        {
            Buffer.BlockCopy(slot.Blocks[(byte)i], 0, raw, i * StreamConstants.MetaBlockDataBytes, StreamConstants.MetaBlockDataBytes);
        }

        if (kind == StreamMetaKind.Cover)
        {
            var length = MeasurePng(raw);
            if (length > 0)
            {
                slot.Bytes = raw[..length];
                slot.Accepted = true;
                return;
            }
        }
        else if (TryDecodeText(raw, out var text))
        {
            slot.Text = text;
            slot.Accepted = true;
            return;
        }

        DiscardedCount++;
        slot.Blocks.Clear();
    }

    /// <summary>
    /// 末尾の 0 パディングを除いたバイト列を厳密な UTF-8 として復号します。
    /// </summary>
    /// <param name="raw">ブロックを連結したバイト列。</param>
    /// <param name="text">復号した文字列。</param>
    /// <returns>途中に 0 が無く、UTF-8 として正しければ true。</returns>
    private static bool TryDecodeText(byte[] raw, out string text)
    {
        text = string.Empty;
        var length = raw.Length;
        while (length > 0 && raw[length - 1] == 0)
        {
            length--;
        }

        var body = raw.AsSpan(0, length);
        if (body.IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(body);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// 先頭から PNG のチャンク構造と各チャンクの CRC-32 を検証し、IEND までの長さを返します。
    /// </summary>
    /// <param name="data">ブロックを連結したバイト列（末尾は 0 パディング）。</param>
    /// <returns>IEND チャンク末尾までのバイト数。PNG として不正なら -1。</returns>
    private static int MeasurePng(byte[] data)
    {
        if (data.Length < PngSignature.Length || !data.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
        {
            return -1;
        }

        var pos = PngSignature.Length;
        var first = true;
        while (pos + 12 <= data.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            if (length > (uint)(data.Length - pos - 12))
            {
                return -1;
            }

            var type = data.AsSpan(pos + 4, 4);
            if (first && !type.SequenceEqual("IHDR"u8))
            {
                return -1;
            }

            var body = data.AsSpan(pos + 4, 4 + (int)length);
            if (!Crc32.Matches(body, data.AsSpan(pos + 8 + (int)length, 4)))
            {
                return -1;
            }

            pos += 12 + (int)length;
            if (type.SequenceEqual("IEND"u8))
            {
                return pos;
            }

            first = false;
        }

        return -1;
    }

    /// <summary>1 種別分の受信ブロックと検証済みの値です。</summary>
    private sealed class MetaSlot
    {
        /// <summary>ブロック位置から 16 バイトデータへの対応。</summary>
        public Dictionary<byte, byte[]> Blocks { get; } = new();

        /// <summary>総ブロック数。</summary>
        public int Total { get; set; } = 1;

        /// <summary>このストリームでブロックを 1 つでも受信したか。</summary>
        public bool Seen { get; set; }

        /// <summary>そろって検証に通ったか。</summary>
        public bool Accepted { get; set; }

        /// <summary>検証済みテキスト（タイトル／アーティスト）。</summary>
        public string Text { get; set; } = string.Empty;

        /// <summary>検証済みバイト列（ジャケ写）。</summary>
        public byte[] Bytes { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// 蓄積と検証済みの値をすべて破棄します。
        /// </summary>
        public void Clear()
        {
            Blocks.Clear();
            Total = 1;
            Seen = false;
            Accepted = false;
            Text = string.Empty;
            Bytes = Array.Empty<byte>();
        }
    }
}
