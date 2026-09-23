using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Onta.Core;
using Onta.History;
using Onta.View.Core;

namespace Onta.View.History;

public partial class HistoryPanel : UserControl
{
    private readonly ObservableCollection<HistoryRow> _receiveRows = [];
    private readonly ObservableCollection<HistoryRow> _sendRows = [];
    private readonly ObservableCollection<UncompleteBlockRow> _uncompleteRows = [];

    /// <summary>
    /// 履歴パネルを初期化し、グリッドと Loaded 時の再読込を設定します。
    /// </summary>
    public HistoryPanel()
    {
        InitializeComponent();
        ReceiveHistoryGrid.ItemsSource = _receiveRows;
        SendHistoryGrid.ItemsSource = _sendRows;
        UncompleteBlockGrid.ItemsSource = _uncompleteRows;
        Loaded += (_, _) => ReloadHistory();
        UpdateButtons();
    }

    /// <summary>
    /// Onta_history.bin から送受信・不明ブロックを再読込して各グリッドへ反映します。
    /// </summary>
    public void ReloadHistory()
    {
        _receiveRows.Clear();
        _sendRows.Clear();
        _uncompleteRows.Clear();

        var entries = HistoryService.LoadEntries(AppPaths.ReceiveHistoryFilePath).ToArray();

        var receiveEntries = entries
            .Where(x => x.Kind == HistoryEntryKind.Receive)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .ToArray();
        var sendEntries = entries
            .Where(x => x.Kind == HistoryEntryKind.Send)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .ToArray();

        foreach (var entry in receiveEntries)
        {
            _receiveRows.Add(new HistoryRow(entry));
            AddUncompleteRows(entry);
        }

        foreach (var entry in sendEntries)
        {
            _sendRows.Add(new HistoryRow(entry));
        }

        var sortedUncomplete = _uncompleteRows
            .OrderByDescending(x => x.SortAtUtc)
            .ThenBy(x => x.BlockHashText, StringComparer.Ordinal)
            .ToArray();
        _uncompleteRows.Clear();
        foreach (var row in sortedUncomplete)
        {
            _uncompleteRows.Add(row);
        }

        StatusText.Text = $"受信 {_receiveRows.Count} 件 / 送信 {_sendRows.Count} 件 / 不明ブロック {_uncompleteRows.Count} 件";
        UpdateButtons();
    }

    /// <summary>
    /// 再読込ボタン押下時に履歴を再読込します。
    /// </summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
    }

    /// <summary>
    /// 受信行のダウンロードボタン押下時に、保存ダイアログ経由でペイロードを書き出します。
    /// </summary>
    /// <param name="sender">イベント発生元（Button.Tag に ReceiveHistoryEntry）。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnRowDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReceiveHistoryEntry entry })
        {
            return;
        }

        SavePayloadWithDialog(entry);
    }

    /// <summary>
    /// 受信日時クリックで、その行のブロック明細を開閉します。
    /// </summary>
    /// <param name="sender">クリックされた表示要素。</param>
    /// <param name="e">マウスボタンイベント引数。</param>
    private void OnReceiveTimestampClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DependencyObject source)
        {
            return;
        }

        var row = FindAncestor<DataGridRow>(source);
        if (row is null)
        {
            return;
        }

        var open = row.DetailsVisibility != Visibility.Visible;
        row.DetailsVisibility = open ? Visibility.Visible : Visibility.Collapsed;

        var grid = FindAncestor<DataGrid>(source);
        if (open)
        {
            row.IsSelected = true;
        }
        else
        {
            // 閉じたあとにセル選択色が残らないよう解除する
            row.IsSelected = false;
            if (grid is not null)
            {
                grid.UnselectAll();
                grid.CurrentCell = default;
            }
        }

        e.Handled = true;
    }

    /// <summary>
    /// 視覚ツリーを遡って指定型の祖先を探します。
    /// </summary>
    /// <typeparam name="T">探す祖先の型。</typeparam>
    /// <param name="current">探索開始ノード。</param>
    /// <returns>見つかった祖先。無ければ null。</returns>
    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>
    /// 保存ダイアログを開き、受信エントリのペイロードをファイルへ書き出します。
    /// </summary>
    /// <param name="entry">書き出し対象の受信履歴エントリ。</param>
    private void SavePayloadWithDialog(ReceiveHistoryEntry entry)
    {
        if (!HistoryService.CanExportPayload(entry))
        {
            StatusText.Text = "履歴データから復元可能なデータがありません。";
            return;
        }

        var defaultName = string.IsNullOrWhiteSpace(entry.FileName)
            ? $"history_{entry.EntryId}.bin"
            : entry.FileName;
        var dlg = new SaveFileDialog
        {
            Title = "ファイルを保存",
            FileName = defaultName,
            Filter = "すべてのファイル (*.*)|*.*|バイナリ (*.bin)|*.bin",
            AddExtension = true,
            DefaultExt = Path.GetExtension(defaultName)
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            if (HistoryService.ExportPayloadToFile(entry, dlg.FileName))
            {
                StatusText.Text = $"保存: {dlg.FileName}";
            }
            else
            {
                StatusText.Text = "保存対象のデータがありません。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失敗: {ex.Message}";
        }
    }

    /// <summary>
    /// 削除ボタン押下時に、選択中エントリを確認ダイアログ後に履歴から削除します。
    /// </summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry is null)
        {
            return;
        }

        var result = MessageBox.Show(
            $"履歴を削除しますか？\n{entry.FileName}\nEntryId={entry.EntryId}",
            "履歴削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (HistoryService.DeleteEntry(AppPaths.ReceiveHistoryFilePath, entry.EntryId))
        {
            StatusText.Text = "削除しました。";
            ReloadHistory();
        }
        else
        {
            StatusText.Text = "削除対象が見つかりません。";
        }
    }

    /// <summary>
    /// グリッド選択変更時に削除ボタンの有効／無効を更新します。
    /// </summary>
    /// <param name="sender">イベント発生元。</param>
    /// <param name="e">選択変更イベント引数。</param>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    /// <summary>
    /// 選択状態に応じて削除ボタンの有効／無効を切り替えます。
    /// </summary>
    private void UpdateButtons()
    {
        DeleteButton.IsEnabled = GetSelectedEntry() is not null;
    }

    /// <summary>
    /// 現在タブの選択行から対応する履歴エントリを取得します。
    /// </summary>
    /// <returns>選択中のエントリ。未選択なら null。</returns>
    private ReceiveHistoryEntry? GetSelectedEntry()
    {
        if (HistoryTabs.SelectedItem == ReceiveHistoryTab)
        {
            return (ReceiveHistoryGrid.SelectedItem as HistoryRow)?.Entry;
        }

        if (HistoryTabs.SelectedItem == SendHistoryTab)
        {
            return (SendHistoryGrid.SelectedItem as HistoryRow)?.Entry;
        }

        if (HistoryTabs.SelectedItem == UncompleteBlockTab)
        {
            return (UncompleteBlockGrid.SelectedItem as UncompleteBlockRow)?.Entry;
        }

        return null;
    }

    /// <summary>
    /// 受信エントリの孤児ブロック（BD 成功かつ親 FH 未解決）を不明ブロック行へ追加します。
    /// </summary>
    /// <param name="entry">孤児を含む受信履歴エントリ。</param>
    private void AddUncompleteRows(ReceiveHistoryEntry entry)
    {
        // 不明ブロック = BD 受信成功かつ親 FH 未解決の孤立のみ。
        // 受信 NG / BH のみの未完了は受信履歴側に残し、ここには出さない。
        foreach (var orphan in entry.Orphans)
        {
            if (orphan.Payload is not { Length: > 0 })
            {
                continue;
            }

            var fileHashText = ResolveFileHashText(entry.ContentHashHex);
            var blockHashText = NormalizeHex(orphan.HashHex);
            var blockPositionText = "-";
            if (ReceiveDetailPanel.TrySplitOrphanIdentity(
                    orphan.HashHex,
                    out var orphanBlockIndex,
                    out var orphanFileHash,
                    out var orphanBlockHash))
            {
                if (fileHashText == "-")
                {
                    fileHashText = NormalizeHex(orphanFileHash);
                }

                blockHashText = NormalizeHex(orphanBlockHash);
                blockPositionText = string.IsNullOrWhiteSpace(orphanBlockIndex) ? "-" : orphanBlockIndex;
            }

            var dm = ParseDataModulation(orphan.DataModulation);
            _uncompleteRows.Add(new UncompleteBlockRow(
                entry,
                resultText: "IN-COMPLETE",
                inputDeviceText: ResolveInputDeviceText(entry),
                subcarrierText: dm.SubcarrierText,
                modulationText: dm.ModulationText,
                channelText: dm.ChannelText,
                blockPositionText: blockPositionText,
                fileHashText: fileHashText,
                blockHashText: blockHashText));
        }
    }

    /// <summary>
    /// 表示用ファイルハッシュを正規化します（旧 FH: プレースホルダは非表示）。
    /// </summary>
    /// <param name="contentHashHex">エントリの ContentHashHex。</param>
    /// <returns>大文字 HEX。無効・プレースホルダなら "-"。</returns>
    private static string ResolveFileHashText(string? contentHashHex)
    {
        if (string.IsNullOrWhiteSpace(contentHashHex)
            || contentHashHex.StartsWith("FH:", StringComparison.Ordinal)
            || contentHashHex.Contains('|', StringComparison.Ordinal)
            || contentHashHex.Contains('\\', StringComparison.Ordinal)
            || contentHashHex.Contains('/', StringComparison.Ordinal))
        {
            return "-";
        }

        return NormalizeHex(contentHashHex);
    }

    /// <summary>
    /// 受信入力デバイスを表示文言へ変換します。
    /// </summary>
    /// <param name="entry">受信履歴エントリ。</param>
    /// <returns>"Audio" または "WAV"。</returns>
    private static string ResolveInputDeviceText(ReceiveHistoryEntry entry)
    {
        return entry.InputDevice == ReceiveInputDevice.Audio ? "Audio" : "WAV";
    }

    /// <summary>
    /// 送信出力デバイスを表示文言へ変換します（OutputPath の有無で判定）。
    /// </summary>
    /// <param name="entry">送信履歴エントリ。</param>
    /// <returns>WAV 出力なら "WAV"、音声出力なら "Audio"。</returns>
    private static string ResolveOutputDeviceText(ReceiveHistoryEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.OutputPath) ? "Audio" : "WAV";
    }

    /// <summary>
    /// 受信元 WAV のファイル名を表示用に取り出します。
    /// </summary>
    /// <param name="entry">受信履歴エントリ。</param>
    /// <returns>ファイル名。音声入力や未設定なら "-"。</returns>
    private static string ResolveSourceWavFileName(ReceiveHistoryEntry entry)
    {
        if (entry.InputDevice != ReceiveInputDevice.Wav)
        {
            return "-";
        }

        if (string.IsNullOrWhiteSpace(entry.SourcePath))
        {
            return "-";
        }

        var name = Path.GetFileName(entry.SourcePath);
        return string.IsNullOrWhiteSpace(name) ? entry.SourcePath : name;
    }

    /// <summary>
    /// 送信 WAV 出力パスからファイル名を表示用に取り出します。
    /// </summary>
    /// <param name="entry">送信履歴エントリ。</param>
    /// <returns>ファイル名。未設定なら "-"。</returns>
    private static string ResolveOutputWavFileName(ReceiveHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.OutputPath))
        {
            return "-";
        }

        var name = Path.GetFileName(entry.OutputPath);
        return string.IsNullOrWhiteSpace(name) ? entry.OutputPath : name;
    }

    /// <summary>
    /// HEX 文字列をトリムして大文字に正規化します。
    /// </summary>
    /// <param name="value">元の HEX 文字列。</param>
    /// <returns>正規化後の HEX。空なら "-"。</returns>
    private static string NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// バイト列を大文字 HEX 文字列へ変換します。
    /// </summary>
    /// <param name="bytes">変換するバイト列。</param>
    /// <returns>HEX 文字列。空なら "-"。</returns>
    private static string ToHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "-";
        }

        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// データ部変調方式 4 バイトを表示用 SC／変調／チャネル文言へ分解します。
    /// </summary>
    /// <param name="dataModulation">サブキャリア数・変調・モノラル/ステレオを含むバイト列。</param>
    /// <returns>表示用の (サブキャリア, 変調, mono/stereo)。不正時は各 "-"。</returns>
    private static (string SubcarrierText, string ModulationText, string ChannelText) ParseDataModulation(byte[]? dataModulation)
    {
        if (dataModulation is null || dataModulation.Length < 3)
        {
            return ("-", "-", "-");
        }

        var sc = OfdmConfig.IsSupportedActiveSubcarriers(dataModulation[0])
            ? dataModulation[0].ToString()
            : "-";
        var modulation = dataModulation[1] switch
        {
            1 => "BPSK",
            2 => "QPSK",
            3 => "16QAM",
            4 => "64QAM",
            _ => "-"
        };
        var channel = dataModulation[2] switch
        {
            0 => "mono",
            1 => "stereo",
            _ => "-"
        };

        return (sc, modulation, channel);
    }

    /// <summary>
    /// UTC 日時をローカル時刻の表示文字列へ変換します。
    /// </summary>
    /// <param name="value">UTC（または Kind 付き）日時。</param>
    /// <returns>"yyyy-MM-dd HH:mm:ss" 形式のローカル時刻。</returns>
    private static string FormatLocalDateTime(DateTime value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// 送受信履歴グリッド用の 1 行データです。
    /// </summary>
    private sealed class HistoryRow
    {
        /// <summary>
        /// 履歴エントリから表示用プロパティを組み立てます。
        /// </summary>
        /// <param name="entry">元となる送受信履歴エントリ。</param>
        public HistoryRow(ReceiveHistoryEntry entry)
        {
            Entry = entry;
            var dm = ParseDataModulation(entry.DataModulation);
            SubcarrierText = dm.SubcarrierText;
            ModulationText = dm.ModulationText;
            ChannelText = dm.ChannelText;
            BlockRows = Entry.Blocks
                .Select(block => new ReceiveBlockRow(block))
                .OrderBy(x => x.SortBlockIndex)
                .ToArray();
        }

        public ReceiveHistoryEntry Entry { get; }
        public string TimestampText => FormatLocalDateTime(Entry.ReceivedAtUtc);
        public string FileName => Entry.FileName;
        public string FileSizeText => Entry.FileSize > 0 ? $"{Entry.FileSize:N0}" : "-";
        public string ResultText => Entry.IsSuccess ? "COMPLETE" : "IN-COMPLETE";
        public string BlockCountText => Entry.BlockCount > 0 ? Entry.BlockCount.ToString() : "-";
        public string InputDeviceText => ResolveInputDeviceText(Entry);
        public string OutputDeviceText => ResolveOutputDeviceText(Entry);
        public string SourceWavFileName => ResolveSourceWavFileName(Entry);
        public string OutputWavFileName => ResolveOutputWavFileName(Entry);
        public bool CanDownload => HistoryService.CanExportPayload(Entry);
        public string CreatedAtText => FormatLocalDateTime(Entry.CreatedAtUtc);
        public string UpdatedAtText => FormatLocalDateTime(Entry.UpdatedAtUtc);
        public string SubcarrierText { get; }
        public string ModulationText { get; }
        public string ChannelText { get; }
        public IReadOnlyList<ReceiveBlockRow> BlockRows { get; }
    }

    /// <summary>
    /// 受信履歴のブロック明細 1 行です。
    /// </summary>
    private sealed class ReceiveBlockRow
    {
        /// <summary>
        /// 受信ブロック履歴から表示用プロパティを組み立てます。
        /// </summary>
        /// <param name="block">元となる受信ブロック履歴。</param>
        public ReceiveBlockRow(ReceiveBlockHistory block)
        {
            SortBlockIndex = block.BlockIndex;
            BlockIndexText = block.BlockIndex.ToString();
            ResultText = IsBlockOk(block) ? "OK" : "NG";
            var dm = ParseDataModulation(block.DataModulation);
            SubcarrierText = dm.SubcarrierText;
            ModulationText = dm.ModulationText;
            ChannelText = dm.ChannelText;
            BlockSizeText = block.BlockSize > 0
                ? block.BlockSize.ToString("N0")
                : (block.BlockData.Length > 0 ? block.BlockData.Length.ToString("N0") : "0");
        }

        public int SortBlockIndex { get; }
        public string BlockIndexText { get; }
        public string ResultText { get; }
        public string SubcarrierText { get; }
        public string ModulationText { get; }
        public string ChannelText { get; }
        public string BlockSizeText { get; }

        /// <summary>
        /// 実データ付きで完了したブロックだけ OK とみなします。
        /// </summary>
        /// <param name="block">判定対象の受信ブロック。</param>
        /// <returns>完了かつサイズまたはデータがあるとき true。</returns>
        private static bool IsBlockOk(ReceiveBlockHistory block)
        {
            if (!block.BlockComplete)
            {
                return false;
            }

            return block.BlockSize > 0 || block.BlockData.Length > 0;
        }
    }

    /// <summary>
    /// 不明ブロック（孤児）グリッド用の 1 行データです。
    /// </summary>
    private sealed class UncompleteBlockRow
    {
        /// <summary>
        /// 表示用文言を保持する不明ブロック行を生成します。
        /// </summary>
        /// <param name="entry">親となる受信履歴エントリ。</param>
        /// <param name="resultText">結果表示（例: IN-COMPLETE）。</param>
        /// <param name="inputDeviceText">入力デバイス表示。</param>
        /// <param name="subcarrierText">サブキャリア数表示。</param>
        /// <param name="modulationText">変調方式表示。</param>
        /// <param name="channelText">mono/stereo 表示。</param>
        /// <param name="blockPositionText">ブロック位置表示。</param>
        /// <param name="fileHashText">ファイルハッシュ表示。</param>
        /// <param name="blockHashText">ブロックハッシュ表示。</param>
        public UncompleteBlockRow(
            ReceiveHistoryEntry entry,
            string resultText,
            string inputDeviceText,
            string subcarrierText,
            string modulationText,
            string channelText,
            string blockPositionText,
            string fileHashText,
            string blockHashText)
        {
            Entry = entry;
            ResultText = resultText;
            InputDeviceText = inputDeviceText;
            SubcarrierText = subcarrierText;
            ModulationText = modulationText;
            ChannelText = channelText;
            BlockPositionText = blockPositionText;
            FileHashText = fileHashText;
            BlockHashText = blockHashText;
        }

        public ReceiveHistoryEntry Entry { get; }
        public DateTime SortAtUtc => Entry.ReceivedAtUtc;
        public string TimestampText => FormatLocalDateTime(Entry.ReceivedAtUtc);
        public string ResultText { get; }
        public string InputDeviceText { get; }
        public string SubcarrierText { get; }
        public string ModulationText { get; }
        public string ChannelText { get; }
        public string BlockPositionText { get; }
        public string FileHashText { get; }
        public string BlockHashText { get; }
    }
}
