using System.Collections.ObjectModel;
using System.IO;
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
        foreach (var block in entry.Blocks)
        {
            if (block.BlockComplete)
            {
                continue;
            }

            var dm = ParseDataModulation(block.DataModulation);
            _uncompleteRows.Add(new UncompleteBlockRow(
                entry,
                resultText: "IN-COMPLETE",
                inputDeviceText: ResolveInputDeviceText(entry),
                subcarrierText: dm.SubcarrierText,
                modulationText: dm.ModulationText,
                channelText: dm.ChannelText,
                fileHashText: NormalizeHex(entry.ContentHashHex),
                blockHashText: ToHex(block.ContentHash)));
        }

        foreach (var orphan in entry.Orphans)
        {
            _uncompleteRows.Add(new UncompleteBlockRow(
                entry,
                resultText: "IN-COMPLETE",
                inputDeviceText: ResolveInputDeviceText(entry),
                subcarrierText: "-",
                modulationText: "-",
                channelText: "-",
                fileHashText: NormalizeHex(entry.ContentHashHex),
                blockHashText: NormalizeHex(orphan.HashHex)));
        }
    }

    private static string ResolveInputDeviceText(ReceiveHistoryEntry entry)
    {
        return entry.InputDevice == ReceiveInputDevice.Audio ? "Audio" : "WAV";
    }

    private static string ResolveOutputDeviceText(ReceiveHistoryEntry entry)
    {
        return string.IsNullOrWhiteSpace(entry.OutputPath) ? "Audio" : "WAV";
    }

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

    private static string ResolveOutputWavFileName(ReceiveHistoryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.OutputPath))
        {
            return "-";
        }

        var name = Path.GetFileName(entry.OutputPath);
        return string.IsNullOrWhiteSpace(name) ? entry.OutputPath : name;
    }

    private static string ResolveDownloadText(ReceiveHistoryEntry entry)
    {
        return entry.IsSuccess && entry.Payload.Length > 0 ? "保存可" : "-";
    }

    private static string NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Trim().ToUpperInvariant();
    }

    private static string ToHex(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return "-";
        }

        return Convert.ToHexString(bytes);
    }

    private static (string SubcarrierText, string ModulationText, string ChannelText) ParseDataModulation(byte[]? dataModulation)
    {
        if (dataModulation is null || dataModulation.Length < 3)
        {
            return ("-", "-", "-");
        }

        var sc = dataModulation[0] is 8 or 16 or 24 or 32 or 40 or 48
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

    private sealed class HistoryRow
    {
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
        public string TimestampText => Entry.ReceivedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string FileName => Entry.FileName;
        public string FileSizeText => Entry.FileSize > 0 ? $"{Entry.FileSize:N0}" : "-";
        public string ResultText => Entry.IsSuccess ? "COMPLETE" : "IN-COMPLETE";
        public string BlockCountText => Entry.BlockCount > 0 ? Entry.BlockCount.ToString() : "-";
        public string InputDeviceText => ResolveInputDeviceText(Entry);
        public string OutputDeviceText => ResolveOutputDeviceText(Entry);
        public string SourceWavFileName => ResolveSourceWavFileName(Entry);
        public string OutputWavFileName => ResolveOutputWavFileName(Entry);
        public string DownloadText => ResolveDownloadText(Entry);
        public string CreatedAtText => Entry.CreatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string UpdatedAtText => Entry.UpdatedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string SubcarrierText { get; }
        public string ModulationText { get; }
        public string ChannelText { get; }
        public IReadOnlyList<ReceiveBlockRow> BlockRows { get; }
    }

    private sealed class ReceiveBlockRow
    {
        public ReceiveBlockRow(ReceiveBlockHistory block)
        {
            SortBlockIndex = block.BlockIndex;
            BlockIndexText = block.BlockIndex.ToString();
            ResultText = block.BlockComplete ? "OK" : "NG";
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
    }

    private sealed class UncompleteBlockRow
    {
        public UncompleteBlockRow(
            ReceiveHistoryEntry entry,
            string resultText,
            string inputDeviceText,
            string subcarrierText,
            string modulationText,
            string channelText,
            string fileHashText,
            string blockHashText)
        {
            Entry = entry;
            ResultText = resultText;
            InputDeviceText = inputDeviceText;
            SubcarrierText = subcarrierText;
            ModulationText = modulationText;
            ChannelText = channelText;
            FileHashText = fileHashText;
            BlockHashText = blockHashText;
        }

        public ReceiveHistoryEntry Entry { get; }
        public DateTime SortAtUtc => Entry.ReceivedAtUtc;
        public string TimestampText => Entry.ReceivedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public string ResultText { get; }
        public string InputDeviceText { get; }
        public string SubcarrierText { get; }
        public string ModulationText { get; }
        public string ChannelText { get; }
        public string FileHashText { get; }
        public string BlockHashText { get; }
    }
}
