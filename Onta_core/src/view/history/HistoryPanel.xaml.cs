using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Onta.History;
using Onta.View.Core;

namespace Onta.View.History;

public partial class HistoryPanel : UserControl
{
    private readonly ObservableCollection<HistoryRow> _rows = [];

    public HistoryPanel()
    {
        InitializeComponent();
        HistoryGrid.ItemsSource = _rows;
        Loaded += (_, _) => ReloadHistory();
        UpdateButtons();
    }

    public void ReloadHistory()
    {
        _rows.Clear();
        var entries = HistoryService.LoadEntries(AppPaths.ReceiveHistoryFilePath)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .ToArray();
        foreach (var entry in entries)
        {
            _rows.Add(new HistoryRow(entry));
        }

        StatusText.Text = $"{_rows.Count} 件";
        UpdateButtons();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
    }

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (HistoryGrid.SelectedItem is not HistoryRow row)
        {
            return;
        }

        if (row.Entry.Payload.Length == 0)
        {
            StatusText.Text = "Payload がありません。";
            return;
        }

        var defaultName = string.IsNullOrWhiteSpace(row.Entry.FileName)
            ? $"history_{row.Entry.EntryId}.bin"
            : row.Entry.FileName;
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
            if (HistoryService.ExportPayloadToFile(row.Entry, dlg.FileName))
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
        if (HistoryGrid.SelectedItem is not HistoryRow row)
        {
            return;
        }

        var result = MessageBox.Show(
            $"履歴を削除しますか？\n{row.Entry.FileName}\nEntryId={row.Entry.EntryId}",
            "履歴削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (HistoryService.DeleteEntry(AppPaths.ReceiveHistoryFilePath, row.Entry.EntryId))
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
        var hasSelection = HistoryGrid.SelectedItem is HistoryRow;
        ExportButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
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
        public string FileName => Entry.FileName;
        public string FileSizeText => Entry.FileSize > 0 ? $"{Entry.FileSize:N0}" : "-";
        public string StatusText => Entry.IsSuccess ? "成功" : "失敗";
        public string PayloadBytesText => Entry.Payload.Length > 0 ? $"{Entry.Payload.Length:N0} bytes" : "-";
    }
}

