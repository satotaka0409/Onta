using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Onta.History;
using Onta.View.Core;

namespace Onta.View.History;

public partial class HistoryPanel : UserControl
{
    private readonly ObservableCollection<HistoryRow> _receiveRows = [];
    private readonly ObservableCollection<HistoryRow> _sendRows = [];
    private readonly ObservableCollection<UncompleteBlockRow> _uncompleteRows = [];

    public HistoryPanel()
    {
        InitializeComponent();
        ReceiveHistoryGrid.ItemsSource = _receiveRows;
        SendHistoryGrid.ItemsSource = _sendRows;
        UncompleteBlockGrid.ItemsSource = _uncompleteRows;
        Loaded += (_, _) => ReloadHistory();
        UpdateButtons();
    }

    public void ReloadHistory()
    {
        _receiveRows.Clear();
        _sendRows.Clear();
        _uncompleteRows.Clear();

        var entries = HistoryService.LoadEntries(AppPaths.ReceiveHistoryFilePath)
            .ToArray();

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

        StatusText.Text = $"受信 {_receiveRows.Count} 件 / 送信 {_sendRows.Count} 件 / 未完了 {_uncompleteRows.Count} 件";
        UpdateButtons();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var entry = GetSelectedEntry();
        if (entry is null)
        {
            return;
        }

        if (entry.Payload.Length == 0)
        {
            StatusText.Text = "Payload がありません。";
            return;
        }

        var defaultName = string.IsNullOrWhiteSpace(entry.FileName)
            ? $"history_{entry.EntryId}.bin"
            : entry.FileName;
        var dlg = new SaveFileDialog
        {
            Title = "履歴Payloadを保存",
            FileName = defaultName,
            Filter = "バイナリ (*.bin)|*.bin|すべてのファイル (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".bin"
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
                StatusText.Text = "保存対象のPayloadがありません。";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失敗: {ex.Message}";
        }
    }

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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var selected = GetSelectedEntry();
        ExportButton.IsEnabled = selected is not null && selected.Payload.Length > 0;
        DeleteButton.IsEnabled = selected is not null;
    }

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

    private void AddUncompleteRows(ReceiveHistoryEntry entry)
    {
        var hasUncomplete = false;
        foreach (var block in entry.Blocks)
        {
            if (block.BlockComplete)
            {
                continue;
            }

            hasUncomplete = true;
            _uncompleteRows.Add(new UncompleteBlockRow(
                entry,
                $"BLK-{block.BlockIndex}",
                FormatDataModulation(block.DataModulation),
                block.State == ReceiveBlockState.Error ? "エラー" : "未完了",
                string.IsNullOrWhiteSpace(block.ErrorText) ? "詳細なし" : block.ErrorText));
        }

        foreach (var orphan in entry.Orphans)
        {
            hasUncomplete = true;
            var orphanDetail = string.IsNullOrWhiteSpace(orphan.Detail) ? "親不明" : orphan.Detail;
            _uncompleteRows.Add(new UncompleteBlockRow(
                entry,
                "ORPHAN",
                "-",
                "親未解決",
                $"{orphanDetail} / {orphan.Payload.Length:N0} bytes"));
        }

        if (!entry.IsSuccess && !hasUncomplete)
        {
            _uncompleteRows.Add(new UncompleteBlockRow(
                entry,
                "-",
                "-",
                "未完了",
                string.IsNullOrWhiteSpace(entry.CompletionMessage) ? "詳細なし" : entry.CompletionMessage));
        }
    }

    private static string FormatDataModulation(byte[]? dataModulation)
    {
        if (dataModulation is null || dataModulation.Length < 3)
        {
            return "-";
        }

        var sc = dataModulation[0] is 8 or 16 or 24 or 32 or 40 or 48
            ? dataModulation[0].ToString()
            : "?";
        var modulation = dataModulation[1] switch
        {
            1 => "BPSK",
            2 => "QPSK",
            3 => "16QAM",
            4 => "64QAM",
            _ => "?"
        };
        var channel = dataModulation[2] switch
        {
            0 => "モノラル",
            1 => "ステレオ",
            _ => "?"
        };

        return $"{sc}/{modulation}/{channel}";
    }

    private sealed class HistoryRow
    {
        public HistoryRow(ReceiveHistoryEntry entry)
        {
            Entry = entry;
        }

        public ReceiveHistoryEntry Entry { get; }
        public string EntryId => Entry.EntryId;
        public string KindText => Entry.Kind == HistoryEntryKind.Receive ? "受信" : "送信";
        public string TimestampText => Entry.ReceivedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string InputDeviceText => Entry.InputDevice == ReceiveInputDevice.Audio ? "音声" : "WAV";
        public string FileName => Entry.FileName;
        public string FileSizeText => Entry.FileSize > 0 ? $"{Entry.FileSize:N0}" : "-";
        public string StatusText => Entry.IsSuccess ? "成功" : "失敗";
        public string PayloadBytesText => Entry.Payload.Length > 0 ? $"{Entry.Payload.Length:N0} bytes" : "-";
    }

    private sealed class UncompleteBlockRow
    {
        public UncompleteBlockRow(
            ReceiveHistoryEntry entry,
            string targetText,
            string dataModulationText,
            string stateText,
            string detailText)
        {
            Entry = entry;
            TargetText = targetText;
            DataModulationText = dataModulationText;
            StateText = stateText;
            DetailText = detailText;
        }

        public ReceiveHistoryEntry Entry { get; }
        public string EntryId => Entry.EntryId;
        public string KindText => Entry.Kind == HistoryEntryKind.Receive ? "受信" : "送信";
        public string TimestampText => Entry.ReceivedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string FileName => Entry.FileName;
        public string TargetText { get; }
        public string DataModulationText { get; }
        public string StateText { get; }
        public string DetailText { get; }
    }
}

