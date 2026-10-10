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
using Onta.View.Language;

namespace Onta.View.History;

public partial class HistoryPanel : UserControl
{
    private readonly ObservableCollection<HistoryRow> _receiveRows = [];
    private readonly ObservableCollection<HistoryRow> _sendRows = [];
    private readonly ObservableCollection<UncompleteBlockRow> _uncompleteRows = [];

    /// <summary>
    /// 履歴パネルを初期化し、読込時に履歴を再読込します。
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
    /// 履歴ファイルを読み込み、一覧へ反映します。
    /// </summary>
    public void ReloadHistory()
    {
        ApplyEntries(
            HistoryService.LoadEntries(AppPaths.ReceiveHistoryFilePath),
            HistoryService.LoadOrphans(AppPaths.ReceiveHistoryFilePath));
    }

    /// <summary>
    /// 受信・送信の各一覧へ履歴エントリを振り分け、不明ブロック一覧を表示します。
    /// </summary>
    /// <param name="entries">履歴エントリ。</param>
    /// <param name="orphans">不明ブロック（親のいない未完了エントリ）。</param>
    internal void ApplyEntries(IReadOnlyList<ReceiveHistoryEntry> entries, IReadOnlyList<ReceiveOrphanHistory> orphans)
    {
        _receiveRows.Clear();
        _sendRows.Clear();
        _uncompleteRows.Clear();

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
        }

        foreach (var orphan in orphans.OrderByDescending(x => x.ReceivedAtUtc))
        {
            _uncompleteRows.Add(new UncompleteBlockRow(orphan));
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

        StatusText.Text = CoreViewText.StatusHistorySummary(_receiveRows.Count, _sendRows.Count, _uncompleteRows.Count);
        UpdateButtons();
    }

    /// <summary>
    /// 再読込ボタンで履歴を読み直します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
    }

    /// <summary>
    /// 行のダウンロードボタンでペイロードの保存ダイアログを開きます。
    /// </summary>
    /// <param name="sender">イベントの発生元。Tag に履歴エントリを持つボタン。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnRowDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReceiveHistoryEntry entry })
        {
            return;
        }

        SavePayloadWithDialog(entry);
    }

    /// <summary>
    /// 受信日時のクリックでブロック明細の表示を切り替えます。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。明細の開閉後に処理済みにします。</param>
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
    /// ビジュアルツリーを遡り、指定した型の祖先を探します。
    /// </summary>
    /// <param name="current">探索を始める要素。</param>
    /// <returns>見つかった祖先。無い場合は null。</returns>
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
    /// 保存ダイアログで受信ペイロードをファイルへ書き出します。
    /// </summary>
    /// <param name="entry">保存する受信履歴。</param>
    private void SavePayloadWithDialog(ReceiveHistoryEntry entry)
    {
        if (!HistoryService.CanExportPayload(entry))
        {
            StatusText.Text = CoreViewText.HistoryNoExportableData;
            return;
        }

        var defaultName = string.IsNullOrWhiteSpace(entry.FileName)
            ? $"history_{entry.EntryId}.bin"
            : entry.FileName;
        var dlg = new SaveFileDialog
        {
            Title = CoreViewText.SaveFileTitle,
            FileName = defaultName,
            Filter = CoreViewText.FilterAllAndBinary,
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
                StatusText.Text = CoreViewText.StatusSaved(dlg.FileName);
            }
            else
            {
                StatusText.Text = CoreViewText.StatusNoDataToSave;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = CoreViewText.StatusSaveFailed(ex.Message);
        }
    }

    /// <summary>
    /// 選択中の履歴を確認のうえ削除します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (HistoryTabs.SelectedItem == UncompleteBlockTab)
        {
            DeleteSelectedOrphan();
            return;
        }

        var entry = GetSelectedEntry();
        if (entry is null)
        {
            return;
        }

        var result = MessageBox.Show(
            CoreViewText.ConfirmDeleteHistory(entry.FileName, entry.EntryId),
            CoreViewText.DeleteHistoryTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (HistoryService.DeleteEntry(AppPaths.ReceiveHistoryFilePath, entry.EntryId))
        {
            StatusText.Text = CoreViewText.StatusDeleted;
            ReloadHistory();
        }
        else
        {
            StatusText.Text = CoreViewText.StatusDeleteTargetNotFound;
        }
    }

    /// <summary>
    /// 不明ブロックタブで選択中の不明ブロックを確認のうえ削除します。
    /// </summary>
    private void DeleteSelectedOrphan()
    {
        if (UncompleteBlockGrid.SelectedItem is not UncompleteBlockRow row)
        {
            return;
        }

        var result = MessageBox.Show(
            CoreViewText.ConfirmDeleteOrphan(row.BlockPositionText, row.BlockHashText),
            CoreViewText.DeleteHistoryTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (HistoryService.DeleteOrphan(AppPaths.ReceiveHistoryFilePath, row.Orphan.HashHex))
        {
            StatusText.Text = CoreViewText.StatusDeleted;
            ReloadHistory();
        }
        else
        {
            StatusText.Text = CoreViewText.StatusDeleteTargetNotFound;
        }
    }

    /// <summary>
    /// 選択変更に合わせてボタンの有効状態を更新します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    /// <summary>
    /// 削除ボタンを、履歴が選択されているときだけ有効にします。
    /// </summary>
    private void UpdateButtons()
    {
        DeleteButton.IsEnabled = HistoryTabs.SelectedItem == UncompleteBlockTab
            ? UncompleteBlockGrid.SelectedItem is UncompleteBlockRow
            : GetSelectedEntry() is not null;
    }

    /// <summary>
    /// 表示中タブで選択されている履歴エントリを返します。
    /// </summary>
    /// <returns>選択中の履歴。未選択なら null。</returns>
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

        return null;
    }

    /// <summary>
    /// 受信入力が音声か WAV かを表示文字列にします。
    /// </summary>
    /// <param name="entry">受信履歴エントリ。</param>
    /// <returns>音声入力なら Audio、WAV 入力なら WAV。</returns>
    private static string ResolveInputDeviceText(ReceiveHistoryEntry entry)
    {
        return entry.InputDevice == ReceiveInputDevice.Audio ? "Audio" : "WAV";
    }

    /// <summary>
    /// 出力が音声か WAV かを表示文字列にします。
    /// </summary>
    /// <param name="entry">履歴エントリ。</param>
    /// <returns>出力パスが空なら Audio、それ以外は WAV。</returns>
    private static string ResolveOutputDeviceText(ReceiveHistoryEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.OutputPath) ? "Audio" : "WAV";
    }

    /// <summary>
    /// 受信元の WAV ファイル名（音声入力ならデバイスの表示名）を返します。
    /// </summary>
    /// <param name="entry">受信履歴エントリ。</param>
    /// <returns>ファイル名。音声入力ならデバイスの表示名。パスが空なら "-"。</returns>
    private static string ResolveSourceWavFileName(ReceiveHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.SourcePath))
        {
            return "-";
        }

        if (entry.InputDevice != ReceiveInputDevice.Wav)
        {
            return entry.SourcePath;
        }

        var name = Path.GetFileName(entry.SourcePath);
        return string.IsNullOrWhiteSpace(name) ? entry.SourcePath : name;
    }

    /// <summary>
    /// WAV 出力ファイル名を返します。
    /// </summary>
    /// <param name="entry">履歴エントリ。</param>
    /// <returns>ファイル名。出力パスが空なら "-"。</returns>
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
    /// 16進文字列の前後空白を除き、大文字にします。
    /// </summary>
    /// <param name="value">元の16進文字列。</param>
    /// <returns>正規化した文字列。空なら "-"。</returns>
    private static string NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// データ部変調バイト列を、サブキャリア数・変調・チャンネルの表示へ分けます。
    /// </summary>
    /// <param name="dataModulation">変調方式バイト列。長さ不足時は未設定。</param>
    /// <returns>サブキャリア数、変調方式、mono または stereo の表示。</returns>
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
            6 => "8PSK",
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
    /// UTC 日時をローカルの yyyy-MM-dd HH:mm:ss にします。
    /// </summary>
    /// <param name="value">変換する日時。</param>
    /// <returns>ローカル日時の表示文字列。</returns>
    private static string FormatLocalDateTime(DateTime value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    private sealed class HistoryRow
    {
        /// <summary>
        /// 履歴行の表示値を履歴エントリから作ります。
        /// </summary>
        /// <param name="entry">履歴エントリ。</param>
        public HistoryRow(ReceiveHistoryEntry entry)
        {
            Entry = entry;
            var dm = ParseDataModulation(entry.DataModulation);
            SubcarrierText = dm.SubcarrierText;
            ModulationText = dm.ModulationText;
            ChannelText = dm.ChannelText;
            BlockRows = entry.Blocks
                .Select(block => new ReceiveBlockRow(block))
                .OrderBy(x => x.SortBlockIndex)
                .ToArray();
        }

        public ReceiveHistoryEntry Entry { get; }
        public string TimestampText => FormatLocalDateTime(Entry.ReceivedAtUtc);
        public string FileName => Entry.FileName;
        public string FileSizeText => Entry.FileSize > 0 ? $"{Entry.FileSize:N0}" : "-";
        public string ResultText => Entry.IsSuccess ? CoreViewText.Complete : CoreViewText.Incomplete;
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

    private sealed class ReceiveBlockRow
    {
        /// <summary>
        /// 受信ブロック行の表示値をブロック履歴から作ります。
        /// </summary>
        /// <param name="block">受信ブロック履歴。</param>
        public ReceiveBlockRow(ReceiveBlockHistory block)
        {
            SortBlockIndex = block.BlockIndex;
            BlockIndexText = block.BlockIndex.ToString();
            IsOk = IsBlockOk(block);
            ResultText = IsOk ? "OK" : "NG";
            var dm = ParseDataModulation(block.DataModulation);
            SubcarrierText = dm.SubcarrierText;
            ModulationText = dm.ModulationText;
            ChannelText = dm.ChannelText;
            BlockSizeText = block.BlockSize > 0
                ? block.BlockSize.ToString("N0")
                : (block.BlockData.Length > 0 ? block.BlockData.Length.ToString("N0") : "0");
        }

        public int SortBlockIndex { get; }
        public bool IsOk { get; }
        public string BlockIndexText { get; }
        public string ResultText { get; }
        public string SubcarrierText { get; }
        public string ModulationText { get; }
        public string ChannelText { get; }
        public string BlockSizeText { get; }

        /// <summary>
        /// ブロック受信が完了し、サイズまたはデータがあるかを返します。
        /// </summary>
        /// <param name="block">受信ブロック履歴。</param>
        /// <returns>正常なら true。</returns>
        private static bool IsBlockOk(ReceiveBlockHistory block)
        {
            if (!block.BlockComplete)
            {
                return false;
            }

            return block.BlockSize > 0 || block.BlockData.Length > 0;
        }
    }

    private sealed class UncompleteBlockRow
    {
        /// <summary>
        /// 不明ブロック行の表示値を不明ブロックから作ります。
        /// </summary>
        /// <param name="orphan">不明ブロック（親のいない未完了エントリ）。</param>
        public UncompleteBlockRow(ReceiveOrphanHistory orphan)
        {
            Orphan = orphan;
            SortAtUtc = orphan.ReceivedAtUtc;
            ResultText = orphan.IsComplete ? CoreViewText.Complete : CoreViewText.Incomplete;
            InputDeviceText = orphan.InputDevice == ReceiveInputDevice.Audio ? "Audio" : "WAV";
            var dm = ParseDataModulation(orphan.DataModulation);
            SubcarrierText = dm.SubcarrierText;
            ModulationText = dm.ModulationText;
            ChannelText = dm.ChannelText;
            if (ReceiveDetailPanel.TrySplitOrphanIdentity(orphan.HashHex, out var indexText, out var fileHash, out var blockHash))
            {
                BlockPositionText = string.IsNullOrWhiteSpace(indexText) ? "-" : indexText;
                FileHashText = NormalizeHex(fileHash);
                BlockHashText = NormalizeHex(blockHash);
            }
            else
            {
                BlockPositionText = "-";
                FileHashText = "-";
                BlockHashText = NormalizeHex(orphan.HashHex);
            }
        }

        public ReceiveOrphanHistory Orphan { get; }
        public DateTime SortAtUtc { get; }
        public string TimestampText => FormatLocalDateTime(SortAtUtc);
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
