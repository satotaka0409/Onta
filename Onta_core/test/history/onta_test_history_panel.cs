using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Onta.History;
using Onta.View.History;
using Xunit;

namespace Onta.Core.Tests.History;

/// <summary>
/// 履歴画面のグリッド表示です。列に出る文言と、削除・再受信・不明ブロック照合後の見え方を確認します。
/// </summary>
public sealed class OntaTestHistoryPanel
{
    /// <summary>
    /// 送信・受信完了・受信エラー・不明ブロックが、それぞれのタブに表示されること。
    /// </summary>
    [Fact]
    public void Grids_ShowSendReceiveErrorAndUnknown()
    {
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample1.txt"));
        var errorPayload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample2.txt"));
        var fileHash = Sha512(payload);
        var errorHash = Sha512(errorPayload);
        var blockHash = Sha256(payload);
        var when = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc);

        var send = SendEntry("Sample1.txt", payload.Length, @"C:\tmp\Sample1.txt", @"C:\tmp\Sample1.wav", when);
        var receive = ReceiveEntry(
            "Sample1.txt",
            fileHash,
            payload.Length,
            blockCount: 1,
            isSuccess: true,
            sourcePath: @"C:\tmp\in_sample1.wav",
            when: when.AddMinutes(1),
            blocks: [CompleteBlock(0, payload)]);
        var errorChunks = Split(errorPayload, 2);
        var broken = ReceiveEntry(
            "Sample2.txt",
            errorHash,
            errorPayload.Length,
            blockCount: 2,
            isSuccess: false,
            sourcePath: @"C:\tmp\in_sample2.wav",
            when: when.AddMinutes(2),
            blocks: [ErrorBlock(0), CompleteBlock(1, errorChunks[1])]);
        var unknown = ReceiveEntry(
            "(未登録データ)",
            fileHash,
            fileSize: 0,
            blockCount: 0,
            isSuccess: false,
            sourcePath: @"C:\tmp\orphan.wav",
            when: when.AddMinutes(3),
            blocks: [],
            orphans: [Orphan($"0:{fileHash}:{blockHash}", payload)]);

        var view = Show(send, receive, broken, unknown);

        Assert.Equal("受信 3 件 / 送信 1 件 / 不明ブロック 1 件", view.Status);

        var sendRow = view.Send.Single();
        Assert.Equal("Sample1.txt", sendRow["FileName"]);
        Assert.Equal(payload.Length.ToString("N0"), sendRow["FileSizeText"]);
        Assert.Equal("WAV", sendRow["OutputDeviceText"]);
        Assert.Equal("Sample1.wav", sendRow["OutputWavFileName"]);
        Assert.Equal("16", sendRow["SubcarrierText"]);
        Assert.Equal("BPSK", sendRow["ModulationText"]);
        Assert.Equal("mono", sendRow["ChannelText"]);

        Assert.Equal(["(未登録データ)", "Sample2.txt", "Sample1.txt"], view.Receive.Select(row => row.Cells["FileName"]).ToArray());

        var complete = view.Receive.Single(row => row.Cells["FileName"] == "Sample1.txt");
        Assert.Equal("COMPLETE", complete.Cells["ResultText"]);
        Assert.Equal("1", complete.Cells["BlockCountText"]);
        Assert.Equal("WAV", complete.Cells["InputDeviceText"]);
        Assert.Equal("in_sample1.wav", complete.Cells["SourceWavFileName"]);
        Assert.Equal("True", complete.Cells["CanDownload"]);
        Assert.Equal("0", complete.Blocks[0]["BlockIndexText"]);
        Assert.Equal("OK", complete.Blocks[0]["ResultText"]);
        Assert.Equal("16", complete.Blocks[0]["SubcarrierText"]);
        Assert.Equal("BPSK", complete.Blocks[0]["ModulationText"]);
        Assert.Equal("mono", complete.Blocks[0]["ChannelText"]);
        Assert.Equal(payload.Length.ToString("N0"), complete.Blocks[0]["BlockSizeText"]);

        var incomplete = view.Receive.Single(row => row.Cells["FileName"] == "Sample2.txt");
        Assert.Equal("IN-COMPLETE", incomplete.Cells["ResultText"]);
        Assert.Equal("False", incomplete.Cells["CanDownload"]);
        Assert.Equal("NG", incomplete.Blocks[0]["ResultText"]);
        Assert.Equal("0", incomplete.Blocks[0]["BlockSizeText"]);
        Assert.Equal("OK", incomplete.Blocks[1]["ResultText"]);

        var unknownRow = view.Unknown.Single();
        Assert.Equal("IN-COMPLETE", unknownRow["ResultText"]);
        Assert.Equal("WAV", unknownRow["InputDeviceText"]);
        Assert.Equal("16", unknownRow["SubcarrierText"]);
        Assert.Equal("BPSK", unknownRow["ModulationText"]);
        Assert.Equal("mono", unknownRow["ChannelText"]);
        Assert.Equal("0", unknownRow["BlockPositionText"]);
        Assert.Equal(fileHash, unknownRow["FileHashText"]);
        Assert.Equal(blockHash, unknownRow["BlockHashText"]);
    }

    /// <summary>
    /// 削除した送信・受信は、再表示後のグリッドから消えること。
    /// </summary>
    [Fact]
    public void Grids_AfterDelete_DropRemovedRows()
    {
        using var history = HistoryFile.Create();
        var sample1 = TestPaths.ResolveInputTxt("Sample1.txt");
        var sample2 = TestPaths.ResolveInputTxt("Sample2.txt");
        HistoryService.SaveSend(history.Path, sample1, @"C:\tmp\a.wav", "送信1");
        HistoryService.SaveSend(history.Path, sample2, @"C:\tmp\b.wav", "送信2");
        HistoryService.SaveReceive(history.Path, ReceiveEntry(
            "keep.txt",
            Sha512([1, 2, 3, 4]),
            fileSize: 4,
            blockCount: 1,
            isSuccess: true,
            sourcePath: @"C:\tmp\keep.wav",
            when: DateTime.UtcNow,
            blocks: [CompleteBlock(0, [1, 2, 3, 4])]));
        HistoryService.SaveReceive(history.Path, ReceiveEntry(
            "drop.txt",
            Sha512([9, 9, 9, 9]),
            fileSize: 4,
            blockCount: 1,
            isSuccess: true,
            sourcePath: @"C:\tmp\drop.wav",
            when: DateTime.UtcNow.AddMinutes(-1),
            blocks: [CompleteBlock(0, [9, 9, 9, 9])]));

        var before = Show(HistoryService.LoadEntries(history.Path));
        Assert.Equal(2, before.Send.Count);
        Assert.Equal(2, before.Receive.Count);

        var loaded = HistoryService.LoadEntries(history.Path);
        Assert.True(HistoryService.DeleteEntry(history.Path, loaded.Single(entry => entry.FileName == "Sample1.txt").EntryId));
        Assert.True(HistoryService.DeleteEntry(history.Path, loaded.Single(entry => entry.FileName == "drop.txt").EntryId));

        var after = Show(HistoryService.LoadEntries(history.Path));
        Assert.Equal("受信 1 件 / 送信 1 件 / 不明ブロック 0 件", after.Status);
        Assert.Equal("Sample2.txt", after.Send.Single()["FileName"]);
        var kept = after.Receive.Single();
        Assert.Equal("keep.txt", kept.Cells["FileName"]);
        Assert.Equal("COMPLETE", kept.Cells["ResultText"]);
        Assert.Equal("True", kept.Cells["CanDownload"]);
    }

    /// <summary>
    /// エラーブロックが正常再受信されると、明細が OK になりダウンロードできること。
    /// </summary>
    [Fact]
    public void Grids_RecoveredBlock_ShowsOkAndDownload()
    {
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample3.txt"));
        var chunks = Split(payload, 2);
        var fileHash = Sha512(payload);
        var when = DateTime.UtcNow;

        var broken = ReceiveEntry(
            "Sample3.txt",
            fileHash,
            payload.Length,
            blockCount: 2,
            isSuccess: false,
            sourcePath: @"C:\tmp\sample3.wav",
            when: when,
            blocks: [ErrorBlock(0), CompleteBlock(1, chunks[1])]);
        var brokenView = Show(broken);
        var brokenRow = brokenView.Receive.Single();
        Assert.Equal("IN-COMPLETE", brokenRow.Cells["ResultText"]);
        Assert.Equal("False", brokenRow.Cells["CanDownload"]);
        Assert.Equal("NG", brokenRow.Blocks[0]["ResultText"]);

        var recovered = ReceiveEntry(
            "Sample3.txt",
            fileHash,
            payload.Length,
            blockCount: 2,
            isSuccess: true,
            sourcePath: @"C:\tmp\sample3.wav",
            when: when.AddMinutes(1),
            blocks: [CompleteBlock(0, chunks[0]), CompleteBlock(1, chunks[1])]);
        var view = Show(recovered);
        var row = view.Receive.Single();
        Assert.Equal("COMPLETE", row.Cells["ResultText"]);
        Assert.Equal("True", row.Cells["CanDownload"]);
        Assert.Equal("OK", row.Blocks[0]["ResultText"]);
        Assert.Equal("OK", row.Blocks[1]["ResultText"]);
        Assert.Equal(chunks[0].Length.ToString("N0"), row.Blocks[0]["BlockSizeText"]);
        Assert.Empty(view.Unknown);
    }

    /// <summary>
    /// 不明ブロックは照合前は不明タブだけに載り、ファイルヘッダー一致後は受信履歴でダウンロードできること。
    /// </summary>
    [Fact]
    public void Grids_MatchedOrphan_MovesOffUnknownTab()
    {
        using var history = HistoryFile.Create();
        var payload = File.ReadAllBytes(TestPaths.ResolveInputTxt("Sample1.txt"));
        var fileHash = Sha512(payload);
        var blockHash = Sha256(payload);
        var otherHash = Sha512([7, 7, 7]);
        var otherBlockHash = Sha256([7]);
        var source = @"C:\tmp\orphan_src.wav";

        HistoryService.SaveReceive(history.Path, ReceiveEntry(
            "(未登録データ)",
            fileHash,
            fileSize: 0,
            blockCount: 0,
            isSuccess: false,
            sourcePath: source,
            when: DateTime.UtcNow.AddMinutes(-2),
            blocks: [],
            orphans:
            [
                Orphan($"0:{fileHash}:{blockHash}", payload),
                Orphan($"0:{otherHash}:{otherBlockHash}", [7])
            ]));

        var before = Show(HistoryService.LoadEntries(history.Path));
        Assert.Equal("受信 1 件 / 送信 0 件 / 不明ブロック 2 件", before.Status);
        Assert.Equal("False", before.Receive.Single().Cells["CanDownload"]);
        Assert.Equal(2, before.Unknown.Count);

        HistoryService.SaveReceive(history.Path, ReceiveEntry(
            "Sample1.txt",
            fileHash,
            payload.Length,
            blockCount: 1,
            isSuccess: false,
            sourcePath: source,
            when: DateTime.UtcNow,
            blocks: [],
            orphans: []));

        var after = Show(HistoryService.LoadEntries(history.Path));
        Assert.Equal("受信 1 件 / 送信 0 件 / 不明ブロック 1 件", after.Status);
        var row = after.Receive.Single();
        Assert.Equal("Sample1.txt", row.Cells["FileName"]);
        Assert.Equal("COMPLETE", row.Cells["ResultText"]);
        Assert.Equal("True", row.Cells["CanDownload"]);
        Assert.Equal("OK", row.Blocks.Single()["ResultText"]);

        var left = after.Unknown.Single();
        Assert.Equal(otherBlockHash, left["BlockHashText"]);
        Assert.DoesNotContain(after.Unknown, item => item["BlockHashText"] == blockHash);
    }

    /// <summary>
    /// 履歴パネルを STA で作り、列に出る文言をコピーして返します。
    /// </summary>
    /// <param name="entries">表示する履歴。</param>
    /// <returns>ステータスと各タブの表示行。</returns>
    private static HistoryView Show(params ReceiveHistoryEntry[] entries)
    {
        return Show((IReadOnlyList<ReceiveHistoryEntry>)entries);
    }

    /// <summary>
    /// 履歴パネルを STA で作り、列に出る文言をコピーして返します。
    /// </summary>
    /// <param name="entries">表示する履歴。</param>
    /// <returns>ステータスと各タブの表示行。</returns>
    private static HistoryView Show(IReadOnlyList<ReceiveHistoryEntry> entries)
    {
        return StaThread.Run(() =>
        {
            EnsureAppResources();
            var panel = new HistoryPanel();
            panel.ApplyEntries(entries);
            return HistoryView.Capture(panel);
        });
    }

    /// <summary>
    /// HistoryPanel の StaticResource をテスト用 Application に足します。
    /// </summary>
    private static void EnsureAppResources()
    {
        if (Application.Current is null)
        {
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        var resources = Application.Current!.Resources;
        if (!resources.Contains("BrushTextMuted"))
        {
            resources.Add("BrushTextMuted", new SolidColorBrush(Color.FromRgb(0xB0, 0xB5, 0xBF)));
        }
    }

    /// <summary>
    /// 履歴画面からコピーした表示内容です。
    /// </summary>
    /// <param name="Status">件数ステータス。</param>
    /// <param name="Send">送信タブの行。</param>
    /// <param name="Receive">受信タブの行とブロック明細。</param>
    /// <param name="Unknown">不明ブロックタブの行。</param>
    private sealed record HistoryView(
        string Status,
        IReadOnlyList<Dictionary<string, string>> Send,
        IReadOnlyList<ReceiveView> Receive,
        IReadOnlyList<Dictionary<string, string>> Unknown)
    {
        /// <summary>
        /// パネルのグリッドから表示文言を読み取ります。
        /// </summary>
        /// <param name="panel">表示済みの履歴パネル。</param>
        /// <returns>コピーした表示内容。</returns>
        public static HistoryView Capture(HistoryPanel panel)
        {
            return new HistoryView(
                panel.StatusText.Text,
                panel.SendHistoryGrid.Items.Cast<object>().Select(row => Cells(row, "FileName", "FileSizeText", "OutputDeviceText", "OutputWavFileName", "SubcarrierText", "ModulationText", "ChannelText")).ToArray(),
                panel.ReceiveHistoryGrid.Items.Cast<object>().Select(row => new ReceiveView(
                    Cells(row, "FileName", "ResultText", "BlockCountText", "InputDeviceText", "SourceWavFileName", "CanDownload"),
                    Rows(row, "BlockRows").Select(block => Cells(block, "BlockIndexText", "ResultText", "SubcarrierText", "ModulationText", "ChannelText", "BlockSizeText")).ToArray())).ToArray(),
                panel.UncompleteBlockGrid.Items.Cast<object>().Select(row => Cells(row, "ResultText", "InputDeviceText", "SubcarrierText", "ModulationText", "ChannelText", "BlockPositionText", "FileHashText", "BlockHashText")).ToArray());
        }

        /// <summary>
        /// 表示オブジェクトの指定プロパティを文字列辞書にします。
        /// </summary>
        /// <param name="row">グリッド行。</param>
        /// <param name="names">読むプロパティ名。</param>
        /// <returns>プロパティ名と表示文字列。</returns>
        private static Dictionary<string, string> Cells(object row, params string[] names)
        {
            var cells = new Dictionary<string, string>();
            foreach (var name in names)
            {
                var value = row.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row);
                cells[name] = value?.ToString() ?? string.Empty;
            }

            return cells;
        }

        /// <summary>
        /// 行の子一覧を読みます。
        /// </summary>
        /// <param name="row">親行。</param>
        /// <param name="name">一覧プロパティ名。</param>
        /// <returns>子行。</returns>
        private static List<object> Rows(object row, string name)
        {
            var value = row.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(row) as IEnumerable;
            Assert.NotNull(value);
            return value!.Cast<object>().ToList();
        }
    }

    /// <summary>
    /// 受信行と、そのブロック明細です。
    /// </summary>
    /// <param name="Cells">受信行の列。</param>
    /// <param name="Blocks">ブロック明細の列。</param>
    private sealed record ReceiveView(Dictionary<string, string> Cells, IReadOnlyList<Dictionary<string, string>> Blocks);

    /// <summary>
    /// 送信履歴 1 件を作ります。
    /// </summary>
    private static ReceiveHistoryEntry SendEntry(string fileName, long fileSize, string sourcePath, string outputPath, DateTime when)
    {
        return new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Send,
            InputDevice: ReceiveInputDevice.Wav,
            DataModulation: [16, 1, 0, 0],
            ReceivedAtUtc: when,
            CreatedAtUtc: when,
            UpdatedAtUtc: when,
            ContentHashHex: Sha256(new byte[fileSize == 0 ? 1 : (int)Math.Min(fileSize, 8)]),
            SourcePath: sourcePath,
            FileName: fileName,
            FileSize: fileSize,
            BlockCount: 0,
            IsSuccess: true,
            OutputPath: outputPath,
            CompletionMessage: "送信完了",
            Blocks: [],
            Orphans: []);
    }

    /// <summary>
    /// 受信履歴 1 件を作ります。
    /// </summary>
    private static ReceiveHistoryEntry ReceiveEntry(
        string fileName,
        string fileHash,
        long fileSize,
        int blockCount,
        bool isSuccess,
        string sourcePath,
        DateTime when,
        IReadOnlyList<ReceiveBlockHistory> blocks,
        IReadOnlyList<ReceiveOrphanHistory>? orphans = null)
    {
        return new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Receive,
            InputDevice: ReceiveInputDevice.Wav,
            DataModulation: [],
            ReceivedAtUtc: when,
            CreatedAtUtc: when,
            UpdatedAtUtc: when,
            ContentHashHex: fileHash,
            SourcePath: sourcePath,
            FileName: fileName,
            FileSize: fileSize,
            BlockCount: blockCount,
            IsSuccess: isSuccess,
            OutputPath: string.Empty,
            CompletionMessage: isSuccess ? "受信完了" : "未完了",
            Blocks: blocks,
            Orphans: orphans ?? []);
    }

    /// <summary>
    /// 完了ブロックを作ります。
    /// </summary>
    private static ReceiveBlockHistory CompleteBlock(int index, byte[] payload)
    {
        return new ReceiveBlockHistory(
            DataModulation: [16, 1, 0, 0],
            BlockIndex: index,
            BlockSize: payload.Length,
            ContentHash: SHA256.HashData(payload),
            BlockComplete: true,
            BlockData: payload,
            State: ReceiveBlockState.Accepted,
            ErrorText: string.Empty);
    }

    /// <summary>
    /// 未完了のエラーブロックを作ります。
    /// </summary>
    private static ReceiveBlockHistory ErrorBlock(int index)
    {
        return new ReceiveBlockHistory(
            DataModulation: [16, 1, 0, 0],
            BlockIndex: index,
            BlockSize: 0,
            ContentHash: new byte[32],
            BlockComplete: false,
            BlockData: [],
            State: ReceiveBlockState.Error,
            ErrorText: "CRC-ERROR");
    }

    /// <summary>
    /// ペイロード付きの不明ブロックを作ります。
    /// </summary>
    private static ReceiveOrphanHistory Orphan(string hashHex, byte[] payload)
    {
        return new ReceiveOrphanHistory(hashHex, "BH+BD", payload, [16, 1, 0, 0]);
    }

    /// <summary>
    /// ペイロードを等分します。
    /// </summary>
    private static byte[][] Split(byte[] payload, int blockCount)
    {
        var chunks = new byte[blockCount][];
        var baseSize = payload.Length / blockCount;
        var offset = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var size = i == blockCount - 1 ? payload.Length - offset : baseSize;
            chunks[i] = payload.AsSpan(offset, size).ToArray();
            offset += size;
        }

        return chunks;
    }

    private static string Sha512(byte[] payload) => Convert.ToHexString(SHA512.HashData(payload));

    private static string Sha256(byte[] payload) => Convert.ToHexString(SHA256.HashData(payload));

    /// <summary>
    /// 一時履歴ファイルです。
    /// </summary>
    private sealed class HistoryFile : IDisposable
    {
        public string Path { get; }

        private HistoryFile(string path)
        {
            Path = path;
        }

        public static HistoryFile Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "onta_test_history",
                $"history_panel_{Guid.NewGuid():N}.bin");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            return new HistoryFile(path);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // 一時ファイル削除失敗はテスト結果に影響させない。
            }
        }
    }
}
