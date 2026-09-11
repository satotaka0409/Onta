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

internal sealed record ReceiveBlockHistory(
    int BlockIndex,
    ReceiveBlockState State,
    string ErrorText);

internal sealed record ReceiveOrphanHistory(
    string HashHex,
    string Detail,
    byte[] Payload);

internal sealed record ReceiveHistoryEntry(
    string EntryId,
    HistoryEntryKind Kind,
    DateTime ReceivedAtUtc,
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

