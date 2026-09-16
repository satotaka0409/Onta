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

internal sealed record ReceiveOrphanHistory(
    string HashHex,
    string Detail,
    byte[] Payload);

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
    byte[] Payload,
    IReadOnlyList<ReceiveBlockHistory> Blocks,
    IReadOnlyList<ReceiveOrphanHistory> Orphans);

