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
        ApplyEntries(HistoryService.LoadEntries(AppPaths.ReceiveHistoryFilePath));
    }

    internal void ApplyEntries(IReadOnlyList<ReceiveHistoryEntry> entries)
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

        StatusText.Text = CoreViewText.StatusHistorySummary(_receiveRows.Count, _sendRows.Count, _uncompleteRows.Count);
        UpdateButtons();
    }

    private void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadHistory();
    }

    private void OnRowDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ReceiveHistoryEntry entry })
        {
            return;
        }

        SavePayloadWithDialog(entry);
    }

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

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        DeleteButton.IsEnabled = GetSelectedEntry() is not null;
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
                resultText: CoreViewText.Incomplete,
                inputDeviceText: ResolveInputDeviceText(entry),
                subcarrierText: dm.SubcarrierText,
                modulationText: dm.ModulationText,
                channelText: dm.ChannelText,
                blockPositionText: blockPositionText,
                fileHashText: fileHashText,
                blockHashText: blockHashText));
        }
    }

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

    private static string NormalizeHex(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        return value.Trim().ToUpperInvariant();
    }

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

    private static string FormatLocalDateTime(DateTime value)
    {
        return value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
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
