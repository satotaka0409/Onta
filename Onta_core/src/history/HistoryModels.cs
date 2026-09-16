namespace Onta.History;

internal enum HistoryEntryKind : byte
{
    Receive = 1,
    Send = 2
}

internal enum ReceiveBlockState : byte
{
    Unknown = 0,
    Accepted = 1,
    Error = 2
}

internal enum ReceiveInputDevice : byte
{
    Wav = 0,
    Audio = 1
}

internal sealed record ReceiveBlockHistory(
    byte[] DataModulation,
    int BlockIndex,
    int BlockSize,
    byte[] ContentHash,
    bool BlockComplete,
    byte[] BlockData,
    ReceiveBlockState State,
    string ErrorText);

internal sealed record ReceiveCapturedBlockInfo(
    byte[] DataModulation,
    byte[] ContentHash,
    byte[] BlockData);

/// <summary>
/// BH 受信済みで BD 未確定でも持てるブロックメタです。
/// </summary>
internal sealed record ReceiveCapturedBlockHeaderInfo(
    byte[] DataModulation,
    byte[] ContentHash,
    int BlockSize);

internal sealed record ReceiveOrphanHistory(
    string HashHex,
    string Detail,
    byte[] Payload,
    byte[] DataModulation);

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
    IReadOnlyList<ReceiveBlockHistory> Blocks,
    IReadOnlyList<ReceiveOrphanHistory> Orphans);

