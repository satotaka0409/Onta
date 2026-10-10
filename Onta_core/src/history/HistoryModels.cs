namespace Onta.History;

/// <summary>
/// 履歴エントリ種別（受信 / 送信）。
/// </summary>
internal enum HistoryEntryKind : byte
{
    Receive = 1,
    Send = 2
}

/// <summary>
/// 受信ブロックの受理状態。
/// </summary>
internal enum ReceiveBlockState : byte
{
    Unknown = 0,
    Accepted = 1,
    Error = 2
}

/// <summary>
/// 受信入力デバイス（0:WAV / 1:音声）。
/// </summary>
internal enum ReceiveInputDevice : byte
{
    Wav = 0,
    Audio = 1
}

/// <summary>
/// 履歴に保存する受信ブロック 1 件。
/// </summary>
/// <param name="DataModulation">データ部変調 4 バイト（SC / 変調 / チャネル / 予備）。</param>
/// <param name="BlockIndex">ブロック番号。</param>
/// <param name="BlockSize">ブロックバイト数（完了時は BlockData.Length）。</param>
/// <param name="ContentHash">ブロックハッシュ（SHA-256、32 バイト）。</param>
/// <param name="BlockComplete">ブロック受信完了なら true。</param>
/// <param name="BlockData">ブロック本体（未完了時は空）。</param>
/// <param name="State">受理状態。</param>
/// <param name="ErrorText">エラー文言（無ければ空）。</param>
internal sealed record ReceiveBlockHistory(
    byte[] DataModulation,
    int BlockIndex,
    int BlockSize,
    byte[] ContentHash,
    bool BlockComplete,
    byte[] BlockData,
    ReceiveBlockState State,
    string ErrorText);

/// <summary>
/// 受信コアが保持するキャプチャ済みブロック（データ付き）。
/// </summary>
/// <param name="DataModulation">データ部変調 4 バイト。</param>
/// <param name="ContentHash">ブロックハッシュ。</param>
/// <param name="BlockData">ブロック本体。</param>
internal sealed record ReceiveCapturedBlockInfo(
    byte[] DataModulation,
    byte[] ContentHash,
    byte[] BlockData);

/// <summary>
/// BH 受信済みで BD 未確定でも持てるブロックメタです。
/// </summary>
/// <param name="DataModulation">データ部変調 4 バイト。</param>
/// <param name="ContentHash">ブロックハッシュ。</param>
/// <param name="BlockSize">BH 上のブロックサイズ。</param>
internal sealed record ReceiveCapturedBlockHeaderInfo(
    byte[] DataModulation,
    byte[] ContentHash,
    int BlockSize);

/// <summary>
/// 親（FH を受けた受信履歴）がまだ無い不明ブロック（未完了エントリ）。受信履歴とは別に単独で持ちます。
/// </summary>
/// <param name="HashHex">識別子「ブロック位置:ファイルハッシュ:ブロックハッシュ」。</param>
/// <param name="Detail">表示用詳細（履歴には保存しない）。</param>
/// <param name="Payload">ブロック本体。BD 復号に失敗した（BH のみ）ブロックは空。</param>
/// <param name="DataModulation">データ部変調 4 バイト。</param>
/// <param name="ReceivedAtUtc">最後に受信した日時（UTC）。不明なら default。</param>
/// <param name="InputDevice">受信入力デバイス。</param>
/// <param name="SourcePath">受信元 WAV ファイル名、または音声入力デバイスの表示名。</param>
/// <param name="BlockSize">BH 上のブロックサイズ。0 なら Payload の長さ。</param>
internal sealed record ReceiveOrphanHistory(
    string HashHex,
    string Detail,
    byte[] Payload,
    byte[] DataModulation,
    DateTime ReceivedAtUtc = default,
    ReceiveInputDevice InputDevice = ReceiveInputDevice.Wav,
    string SourcePath = "",
    int BlockSize = 0)
{
    /// <summary>BD まで受信できた（データを持つ）なら true。</summary>
    public bool IsComplete => Payload is { Length: > 0 };
}

/// <summary>
/// 送受信履歴の 1 エントリ（バイナリ永続化単位）。
/// </summary>
/// <param name="EntryId">エントリ一意 ID。</param>
/// <param name="Kind">受信 / 送信。</param>
/// <param name="InputDevice">受信入力デバイス（送信時は未使用）。</param>
/// <param name="DataModulation">送信時の変調 4 バイト（受信時は空可）。</param>
/// <param name="ReceivedAtUtc">送受信日時。</param>
/// <param name="CreatedAtUtc">元ファイル作成日時。</param>
/// <param name="UpdatedAtUtc">元ファイル更新日時。</param>
/// <param name="ContentHashHex">ファイルハッシュ（hex）。</param>
/// <param name="SourcePath">受信元 WAV／送信元パス。</param>
/// <param name="FileName">表示用ファイル名。</param>
/// <param name="FileSize">ファイルサイズ（バイト）。</param>
/// <param name="BlockCount">全体ブロック数。</param>
/// <param name="IsSuccess">完了フラグ。</param>
/// <param name="OutputPath">送信 WAV 出力パスなど。</param>
/// <param name="CompletionMessage">完了メッセージ。</param>
/// <param name="Blocks">受信ブロック一覧。</param>
internal sealed record ReceiveHistoryEntry(
    string EntryId,
    HistoryEntryKind Kind,
    ReceiveInputDevice InputDevice,
    byte[] DataModulation,
    DateTime ReceivedAtUtc,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    string ContentHashHex,
    string SourcePath,
    string FileName,
    long FileSize,
    int BlockCount,
    bool IsSuccess,
    string OutputPath,
    string CompletionMessage,
    IReadOnlyList<ReceiveBlockHistory> Blocks);

