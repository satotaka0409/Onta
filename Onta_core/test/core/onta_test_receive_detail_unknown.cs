using System.Security.Cryptography;
using Onta.History;
using Onta.View.Core;
using Onta.View.Language;
using Xunit;

namespace Onta.Core.Tests.Core;

/// <summary>
/// FH 未受信・受信履歴に無いファイルのブロックを、受信詳細が受信したブロックだけの行で表示することを確認します。
/// そのブロックは不明ブロックとして単独で保存するため、受信エントリにはブロックを載せないことも確認します。
/// </summary>
public sealed class OntaTestReceiveDetailUnknown
{
    private static readonly byte[] Sc16Qpsk = [16, 2, 0, 0];

    /// <summary>
    /// 受信したブロックだけが番号順に並び、OK／NG・SC・変調・サイズが出て、Total は OK の合計になること。
    /// NG のブロックを受信し直し始めたら「-」へ戻り、FH が確定したら全ブロックの行構成へ切り替わること。
    /// </summary>
    [Fact]
    public void UnknownFile_ShowsReceivedBlocksOnly()
    {
        StaThread.Run(() =>
        {
            var panel = new ReceiveDetailPanel();
            var headers = new Dictionary<int, ReceiveCapturedBlockHeaderInfo>
            {
                [3] = new(Sc16Qpsk, new byte[32], 8192),
                [1] = new(Sc16Qpsk, new byte[32], 5000),
            };

            panel.SyncBlockHeaders(headers);
            panel.SyncBlockBdOutcomes(new Dictionary<int, bool> { [1] = true, [3] = false });

            var rows = Rows(panel);
            Assert.Equal(6, rows.Count);
            Assert.Equal("-", rows[0].ResultText);
            Assert.Equal("-", rows[1].SizeText);
            Assert.Equal("-", rows[2].SizeText);
            Assert.Equal("BLK-1", rows[3].Name);
            Assert.Equal("OK", rows[3].ResultText);
            Assert.Equal(100, rows[3].ProgressPercent);
            Assert.Equal("5,000", rows[3].SizeText);
            Assert.Equal("16", rows[3].SubcarrierText);
            Assert.Equal("QPSK", rows[3].ModulationText);
            Assert.Equal("mono", rows[3].ChannelText);
            Assert.Equal("BLK-3", rows[4].Name);
            Assert.Equal("NG", rows[4].ResultText);
            Assert.Equal("8,192", rows[4].SizeText);
            Assert.Equal("Total", rows[5].Name);
            Assert.Equal("5,000", rows[5].SizeText);

            var payload = new byte[5000];
            var fileHash = Convert.ToHexString(SHA256.HashData(payload));
            var entry = panel.CaptureHistoryEntry(
                capturedBlocks: new Dictionary<int, ReceiveCapturedBlockInfo>(),
                fileHashHex: fileHash,
                capturedHeaders: headers);
            Assert.Equal(CoreViewText.UnregisteredData, entry.FileName);
            Assert.Equal(fileHash, entry.ContentHashHex);
            Assert.Equal(0, entry.BlockCount);
            Assert.Empty(entry.Blocks);

            panel.SyncBlockBdOutcomes(new Dictionary<int, bool> { [1] = true });
            Assert.Equal("-", Rows(panel)[4].ResultText);

            panel.ApplyFileHeader("a.bin", "29,576", 4, null, null);
            Assert.Equal(["BLK-0", "BLK-1", "BLK-2", "BLK-3"], Rows(panel).Skip(3).Take(4).Select(row => row.Name).ToArray());
            Assert.Equal("Total", Rows(panel)[^1].Name);
        });
    }

    /// <summary>
    /// 受信詳細グリッドの行を返します。
    /// </summary>
    /// <param name="panel">受信詳細パネル。</param>
    /// <returns>表示中の行。</returns>
    private static List<ReceiveDetailPanel.DetailRow> Rows(ReceiveDetailPanel panel)
    {
        return panel.DetailGrid.Items.Cast<ReceiveDetailPanel.DetailRow>().ToList();
    }
}
