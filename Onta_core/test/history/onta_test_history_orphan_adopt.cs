using System.Numerics;
using System.Security.Cryptography;
using Onta.Core;
using Onta.History;
using Onta.View.Core;
using Xunit;

namespace Onta.Core.Tests.History;

/// <summary>
/// 実際の受信信号で、先に不明ブロックが登録され、後から FH を受けて親（受信履歴）と連結されることを確認します。
/// 受信は画面と同じ <see cref="InputCoreWorker"/>、履歴保存は画面（MainWindow）と同じ手順で行います。
/// </summary>
public sealed class OntaTestHistoryOrphanAdopt
{
    private const int SampleRate = 44100;
    private const int BlockSize = FileWavCodec.DataBlockBytes;
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromMinutes(3);

    private static readonly FileWavCodecProfile Profile = new(
        ActiveSubcarriers: 48,
        ModulationScheme: ModulationScheme.Qam16,
        SampleRate: SampleRate,
        ChannelMode: ChannelMode.Stereo,
        BlockInterleaveFactor: 1);

    /// <summary>
    /// 1 回目に FH の無い BH+BD だけを受信すると、受信履歴は作られず全ブロックが不明ブロックとして保存されること。
    /// 2 回目に FH だけを受信すると、不明ブロックが親の受信ブロックへ移って不明ブロックから消え、
    /// 全ブロックがそろった受信履歴は COMPLETE になり、ダウンロードした内容が元ファイルと一致すること。
    /// </summary>
    [Fact]
    public void OrphansFromBlockOnlyReceive_AreAdoptedWhenFileHeaderArrivesLater()
    {
        var payload = new byte[(BlockSize * 2) + 3000];
        new Random(7).NextBytes(payload);
        var fileHash = Convert.ToHexString(SHA256.HashData(payload));
        var blockCount = (payload.Length + BlockSize - 1) / BlockSize;

        var codec = new FileWavCodec(Profile);
        var (left, right, events) = Encode(codec, payload);
        var fileHeaderEnd = (int)events.First(e => e.Kind == TransmissionFrameKind.Fh).End;
        var lastBlockDataEnd = (int)events.Last(e => e.Kind == TransmissionFrameKind.Bd).End;

        var workDir = Path.Combine(Path.GetTempPath(), "onta_test_history", $"orphan_adopt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var historyPath = Path.Combine(workDir, "Onta_history.bin");
        var blockOnlyWav = Path.Combine(workDir, "block_only.wav");
        var fileHeaderOnlyWav = Path.Combine(workDir, "file_header_only.wav");
        WriteSegment(blockOnlyWav, left, right, fileHeaderEnd, lastBlockDataEnd);
        WriteSegment(fileHeaderOnlyWav, left, right, 0, fileHeaderEnd);

        try
        {
            StaThread.Run(() =>
            {
                using var worker = new InputCoreWorker(historyPath);

                ReceiveAndSave(worker, blockOnlyWav, workDir, historyPath);

                Assert.DoesNotContain(HistoryService.LoadEntries(historyPath), e => e.Kind == HistoryEntryKind.Receive);
                var orphans = HistoryService.LoadOrphans(historyPath);
                Assert.Equal(blockCount, orphans.Count);
                for (var i = 0; i < blockCount; i++)
                {
                    var expected = BlockOf(payload, i);
                    var orphan = Assert.Single(orphans, o => o.HashHex.StartsWith($"{i}:", StringComparison.Ordinal));
                    Assert.True(orphan.IsComplete);
                    Assert.Equal($"{i}:{fileHash}:{Convert.ToHexString(SHA256.HashData(expected))}", orphan.HashHex, ignoreCase: true);
                    Assert.Equal(expected, orphan.Payload);
                    Assert.Equal(ReceiveInputDevice.Wav, orphan.InputDevice);
                    Assert.Equal(blockOnlyWav, orphan.SourcePath);
                }

                Assert.False(HistoryService.TryLoadCompletedPayload(historyPath, fileHash, out _, out _));

                ReceiveAndSave(worker, fileHeaderOnlyWav, workDir, historyPath);

                Assert.Empty(HistoryService.LoadOrphans(historyPath));
                var parent = Assert.Single(HistoryService.LoadEntries(historyPath), e => e.Kind == HistoryEntryKind.Receive);
                Assert.Equal(fileHash, parent.ContentHashHex, ignoreCase: true);
                Assert.Equal(fileHeaderOnlyWav, parent.SourcePath);
                Assert.Equal(payload.Length, parent.FileSize);
                Assert.Equal(blockCount, parent.BlockCount);
                Assert.True(parent.IsSuccess);
                Assert.Equal(Enumerable.Range(0, blockCount), parent.Blocks.Select(b => b.BlockIndex).Order());
                foreach (var block in parent.Blocks)
                {
                    Assert.True(block.BlockComplete);
                    Assert.Equal(BlockOf(payload, block.BlockIndex), block.BlockData);
                }

                var exportPath = Path.Combine(workDir, "export.bin");
                Assert.True(HistoryService.ExportPayloadToFile(parent, exportPath));
                Assert.Equal(payload, File.ReadAllBytes(exportPath));

                // FH だけの受信は単独では未完了でも、履歴と合わせてそろったので出力フォルダーへ書き出せる
                Assert.True(HistoryService.TryLoadCompletedPayload(historyPath, fileHash.ToLowerInvariant(), out var fileName, out var completed));
                Assert.Equal(parent.FileName, fileName);
                var receivedPath = ReceivedFileWriter.Write(Path.Combine(workDir, "out"), fileName, completed);
                Assert.Equal(payload, File.ReadAllBytes(receivedPath));
            });
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // 一時フォルダー削除失敗はテスト結果に影響させない。
            }
        }
    }

    /// <summary>
    /// WAV を受信して完了まで待ち、画面（MainWindow）と同じ手順で受信詳細を組み立てて履歴へ保存します。
    /// </summary>
    /// <param name="worker">受信ワーカー。</param>
    /// <param name="wavPath">受信する WAV。</param>
    /// <param name="outputDir">受信の出力フォルダー。</param>
    /// <param name="historyPath">保存先の履歴ファイル。</param>
    private static void ReceiveAndSave(InputCoreWorker worker, string wavPath, string outputDir, string historyPath)
    {
        (string FileName, string FileSizeText, int BlockCount, DateTime? CreatedAtUtc, DateTime? UpdatedAtUtc)? fileHeader = null;
        void OnFileHeaderReady(string name, string sizeText, int blocks, DateTime? createdAtUtc, DateTime? updatedAtUtc)
        {
            fileHeader = (name, sizeText, blocks, createdAtUtc, updatedAtUtc);
        }

        worker.FileHeaderReady += OnFileHeaderReady;
        try
        {
            var panel = new ReceiveDetailPanel();
            panel.SetSource(wavPath, ReceiveInputDevice.Wav);
            Assert.True(worker.TryStartWavDecode(wavPath, CodecProfileFactory.ForWavReceive(wavPath), outputDir));

            var deadline = DateTime.UtcNow + ReceiveTimeout;
            bool success;
            string message;
            string? outputPath;
            while (!worker.TryConsumeCompletion(out success, out message, out outputPath))
            {
                Assert.True(DateTime.UtcNow < deadline, $"受信が完了しません: {wavPath}");
                Thread.Sleep(50);
            }

            if (fileHeader is { } header)
            {
                panel.ApplyFileHeader(header.FileName, header.FileSizeText, header.BlockCount, header.CreatedAtUtc, header.UpdatedAtUtc);
            }

            panel.SyncBlockHeaders(worker.CaptureReceivedBlockHeaders());
            panel.SyncCapturedBlocks(worker.CaptureReceivedBlocks());
            panel.SyncBlockBdOutcomes(worker.CaptureBlockBdOutcomes());
            panel.ApplyStatus(worker.SharedStatus.Read());
            panel.MarkCompletion(success, message, outputPath);

            var entry = panel.CaptureHistoryEntry(
                worker.CaptureReceivedBlocks(),
                worker.CaptureFileHashHex(),
                worker.CaptureReceivedBlockHeaders());
            var orphans = worker.CaptureOrphans()
                .Select(orphan => orphan with { InputDevice = entry.InputDevice, SourcePath = entry.SourcePath })
                .ToArray();
            var parent = entry.BlockCount > 0 ? entry : null;
            if (parent is not null || orphans.Length > 0)
            {
                HistoryService.SaveReceive(historyPath, parent, orphans);
            }
        }
        finally
        {
            worker.FileHeaderReady -= OnFileHeaderReady;
        }
    }

    /// <summary>
    /// ペイロードを送信波形へ符号化し、各フレームの終端サンプル位置も返します。
    /// </summary>
    private static (Complex[] Left, Complex[] Right, List<(TransmissionFrameKind Kind, long End)> Events) Encode(
        FileWavCodec codec,
        byte[] payload)
    {
        var path = Path.Combine(Path.GetTempPath(), $"onta_orphan_adopt_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, payload);
        try
        {
            long position = 0;
            var events = new List<(TransmissionFrameKind Kind, long End)>();
            var (left, right) = codec.EncodeFileToSamples(
                payload,
                new FileInfo(path),
                onFrameTransmitted: kind => events.Add((kind, position)),
                onPcmChunk: (l, _) => position += l.Length);
            return (left, right, events);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// 送信波形の [start, end) を、末尾に 1 秒の無音を足したステレオ WAV として書き出します。
    /// </summary>
    private static void WriteSegment(string path, Complex[] left, Complex[] right, int start, int end)
    {
        var length = end - start + SampleRate;
        var segmentLeft = new Complex[length];
        var segmentRight = new Complex[length];
        Array.Copy(left, start, segmentLeft, 0, end - start);
        Array.Copy(right, start, segmentRight, 0, end - start);
        WavWriter.WriteStereo16(path, SampleRate, segmentLeft, segmentRight, Profile.SamplePeak);
    }

    /// <summary>
    /// ペイロードの指定ブロックの中身を返します。
    /// </summary>
    private static byte[] BlockOf(byte[] payload, int blockIndex)
    {
        var start = blockIndex * BlockSize;
        return payload[start..Math.Min(payload.Length, start + BlockSize)];
    }
}
