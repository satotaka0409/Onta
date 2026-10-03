using System.Security.Cryptography;
using Onta.Core;
using Onta.History;
using Xunit;

namespace Onta.Core.Tests.History;

/// <summary>
/// 履歴画面スクリーンショット用のダミーデータを作成します。
/// </summary>
/// <remarks>
/// 通常のテスト実行では一時フォルダーへ作成し、アプリの Onta_history.bin には触れません。
/// 環境変数 <c>ONTA_HISTORY_SHOT=1</c> のときだけ、アプリの Onta_history.bin をダミーデータで置き換えます（バックアップは作りません）。
/// </remarks>
public sealed class HistoryShotDataGenerator
{
    /// <summary>アプリの履歴ファイルへ書き込むときに立てる環境変数。</summary>
    private const string WriteAppHistoryVariable = "ONTA_HISTORY_SHOT";

    /// <summary>
    /// 送信 2 件・受信 3 件（完了・未完了・不明ブロック）のダミー履歴を作成します。
    /// </summary>
    [Fact]
    public void CreateHistoryShotData()
    {
        var root = ResolveRepoRoot();
        var writeAppHistory = Environment.GetEnvironmentVariable(WriteAppHistoryVariable) == "1";
        var historyDir = writeAppHistory
            ? root
            : Path.Combine(Path.GetTempPath(), "onta_history_shot", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(historyDir);
        var historyPath = Path.Combine(historyDir, "Onta_history.bin");
        var outDir = Path.Combine(historyDir, "out_files");
        Directory.CreateDirectory(outDir);
        if (File.Exists(historyPath))
        {
            File.Delete(historyPath);
        }

        var input1 = Path.Combine(root, "Onta_core", "test", "in_files", "Sample1.txt");
        var input2 = Path.Combine(root, "Onta_core", "test", "in_files", "Sample2.txt");

        HistoryService.SaveSend(
            historyPath,
            input1,
            Path.Combine(outDir, "history_shot_send_1.wav"),
            "send complete (historyshot)");
        HistoryService.SaveSend(
            historyPath,
            input2,
            Path.Combine(outDir, "history_shot_send_2.wav"),
            "send complete (historyshot)");

        var baseAt = DateTime.UtcNow.Date.AddHours(9);
        var payloadA = System.Text.Encoding.UTF8.GetBytes("history-shot-payload-A");
        var payloadB = System.Text.Encoding.UTF8.GetBytes("history-shot-payload-B");
        var payloadC = System.Text.Encoding.UTF8.GetBytes("history-shot-payload-C");

        HistoryService.SaveReceive(historyPath, ReceiveEntry(
            fileName: "project_plan_v2.docx",
            sourcePath: @"C:\recordings\meeting_take1.wav",
            when: baseAt.AddMinutes(1),
            isSuccess: true,
            payload: payloadA,
            blocks:
            [
                CompleteBlock(0, payloadA)
            ],
            orphans: []));

        var half = Math.Max(1, payloadB.Length / 2);
        var payloadB1 = payloadB.Take(half).ToArray();

        HistoryService.SaveReceive(historyPath, ReceiveEntry(
            fileName: "sample_photo.png",
            sourcePath: @"C:\recordings\hall_noise.wav",
            when: baseAt.AddMinutes(2),
            isSuccess: false,
            payload: payloadB,
            blocks:
            [
                CompleteBlock(0, payloadB1),
                ErrorBlock(1, "sync error")
            ],
            orphans: []));

        HistoryService.SaveReceive(historyPath, ReceiveEntry(
            fileName: "(unknown-data)",
            sourcePath: @"C:\recordings\unknown_capture.wav",
            when: baseAt.AddMinutes(3),
            isSuccess: false,
            payload: payloadC,
            blocks: [],
            orphans:
            [
                Orphan(payloadC)
            ]));

        Assert.True(File.Exists(historyPath));
        Assert.True(HistoryService.LoadEntries(historyPath).Count >= 5);
        if (!writeAppHistory)
        {
            Directory.Delete(historyDir, recursive: true);
        }
    }

    private static ReceiveHistoryEntry ReceiveEntry(
        string fileName,
        string sourcePath,
        DateTime when,
        bool isSuccess,
        byte[] payload,
        IReadOnlyList<ReceiveBlockHistory> blocks,
        IReadOnlyList<ReceiveOrphanHistory> orphans)
    {
        return new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Receive,
            InputDevice: ReceiveInputDevice.Wav,
            DataModulation: DataModulation(),
            ReceivedAtUtc: when,
            CreatedAtUtc: when,
            UpdatedAtUtc: when,
            ContentHashHex: ToHex(Sha512(payload)),
            SourcePath: sourcePath,
            FileName: fileName,
            FileSize: payload.Length,
            BlockCount: blocks.Count,
            IsSuccess: isSuccess,
            OutputPath: string.Empty,
            CompletionMessage: isSuccess ? "receive complete (historyshot)" : "receiving (historyshot)",
            Blocks: blocks,
            Orphans: orphans);
    }

    private static ReceiveBlockHistory CompleteBlock(int blockIndex, byte[] payload)
    {
        return new ReceiveBlockHistory(
            DataModulation: DataModulation(),
            BlockIndex: blockIndex,
            BlockSize: payload.Length,
            ContentHash: Sha256(payload),
            BlockComplete: true,
            BlockData: payload,
            State: ReceiveBlockState.Accepted,
            ErrorText: string.Empty);
    }

    private static ReceiveBlockHistory ErrorBlock(int blockIndex, string errorText)
    {
        return new ReceiveBlockHistory(
            DataModulation: DataModulation(),
            BlockIndex: blockIndex,
            BlockSize: 0,
            ContentHash: new byte[32],
            BlockComplete: false,
            BlockData: Array.Empty<byte>(),
            State: ReceiveBlockState.Error,
            ErrorText: errorText);
    }

    private static ReceiveOrphanHistory Orphan(byte[] payload)
    {
        var fileHash = ToHex(Sha512(payload));
        var blockHash = ToHex(Sha256(payload));
        return new ReceiveOrphanHistory(
            HashHex: $"0:{fileHash}:{blockHash}",
            Detail: "orphan for screenshot",
            Payload: payload,
            DataModulation: DataModulation());
    }

    private static byte[] DataModulation()
    {
        return
        [
            (byte)16,
            (byte)ModulationScheme.Bpsk,
            (byte)ChannelMode.Mono,
            0
        ];
    }

    private static byte[] Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    private static byte[] Sha512(byte[] data)
    {
        using var sha = SHA512.Create();
        return sha.ComputeHash(data);
    }

    private static string ToHex(byte[] bytes)
    {
        return Convert.ToHexString(bytes);
    }

    private static string ResolveRepoRoot()
    {
        var known = @"C:\proj\Onta";
        if (File.Exists(Path.Combine(known, "Onta.slnx")))
        {
            return known;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Onta.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("repo root not found");
    }
}
