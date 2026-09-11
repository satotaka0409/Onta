using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Onta.Core;
using Onta.History;

namespace Onta.View.Core;

/// <summary>
/// 受信の詳細進捗（FH/各ブロック/合計）を表示・履歴化するパネルです。
/// </summary>
public partial class ReceiveDetailPanel : UserControl
{
    private readonly ObservableCollection<DetailRow> _rows = [];
    private DetailRow? _fhRow;
    private DetailRow? _totalRow;
    private readonly List<DetailRow> _blockRows = [];
    private readonly Dictionary<string, DetailRow> _orphanRowsByHash = new(StringComparer.Ordinal);
    private ReceiveBlockState[] _blockStates = Array.Empty<ReceiveBlockState>();
    private string[] _blockErrors = Array.Empty<string>();
    private string _sourcePath = string.Empty;
    private bool _lastCompletedSuccess;
    private string _lastCompletionMessage = string.Empty;
    private string _lastOutputPath = string.Empty;
    private int _builtBlockCount = -1;

    /// <summary>
    /// 受信詳細パネルを初期化します。
    /// </summary>
    public ReceiveDetailPanel()
    {
        InitializeComponent();
        DetailGrid.ItemsSource = _rows;
        Clear();
    }

    /// <summary>
    /// 表示状態を初期化します。
    /// </summary>
    public void Clear()
    {
        _rows.Clear();
        _blockRows.Clear();
        _orphanRowsByHash.Clear();
        _blockStates = Array.Empty<ReceiveBlockState>();
        _blockErrors = Array.Empty<string>();
        _sourcePath = string.Empty;
        _lastCompletedSuccess = false;
        _lastCompletionMessage = string.Empty;
        _lastOutputPath = string.Empty;
        _fhRow = null;
        _totalRow = null;
        _builtBlockCount = -1;
        _rows.Add(DetailRow.Info("File Name", "(Not received)"));
    }

    /// <summary>
    /// 履歴保存時に使う入力ソースパスを設定します。
    /// </summary>
    /// <param name="sourcePath">入力ソースパス。</param>
    public void SetSourcePath(string sourcePath)
    {
        _sourcePath = sourcePath ?? string.Empty;
    }

    /// <summary>
    /// 直近完了結果を保持します。
    /// </summary>
    /// <param name="success">成功可否。</param>
    /// <param name="message">完了メッセージ。</param>
    /// <param name="outputPath">出力パス。</param>
    public void MarkCompletion(bool success, string message, string? outputPath)
    {
        _lastCompletedSuccess = success;
        _lastCompletionMessage = message ?? string.Empty;
        _lastOutputPath = outputPath ?? string.Empty;
    }

    /// <summary>
    /// FH確定情報を受信詳細へ反映します。
    /// </summary>
    /// <param name="fileName">受信ファイル名。</param>
    /// <param name="fileSizeText">表示用ファイルサイズ。</param>
    /// <param name="blockCount">ブロック数。</param>
    public void ApplyFileHeader(string fileName, string fileSizeText, int blockCount)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "(不明)" : fileName;
        var size = string.IsNullOrWhiteSpace(fileSizeText) ? "-" : fileSizeText;
        var blocks = Math.Max(0, blockCount);
        EnsureRows(name, size, blocks > 0 ? blocks.ToString() : "-", blocks);
        if (_fhRow is not null)
        {
            _fhRow.ProgressPercent = 100;
        }
    }

    /// <summary>
    /// Core の受信状態を詳細表示へ反映します。
    /// </summary>
    /// <param name="status">Core 実行状態。</param>
    public void ApplyStatus(CoreExecutionStatus status)
    {
        // FH未確定中は既存行がある場合のみ進捗を緩やかに更新する。
        if (!HasFileHeaderInfo(status))
        {
            if (_builtBlockCount < 0 && _rows.Count <= 1)
            {
                return;
            }

            if (_fhRow is not null && status.IsRunning)
            {
                _fhRow.ProgressPercent = Math.Clamp(status.Progress.ProgressPercent, 0.0, 99.0);
            }

            return;
        }

        var fileName = string.IsNullOrWhiteSpace(status.FileName) || status.FileName == "(未受信)"
            ? "(不明)"
            : status.FileName;
        var fileSize = string.IsNullOrWhiteSpace(status.FileSizeText) ? "-" : status.FileSizeText;
        var blockCountText = string.IsNullOrWhiteSpace(status.BlockCountText) ? "-" : status.BlockCountText;
        var totalBlocks = Math.Max(0, status.Progress.TotalBlockCount);

        EnsureRows(fileName, fileSize, blockCountText, totalBlocks);

        // FH行はブロック総数の確定有無で表示を切り替える。
        if (_fhRow is not null)
        {
            if (totalBlocks > 0)
            {
                _fhRow.ProgressPercent = 100;
            }
            else if (status.IsRunning)
            {
                _fhRow.ProgressPercent = Math.Clamp(status.Progress.ProgressPercent, 0.0, 99.0);
            }
            else
            {
                _fhRow.ProgressPercent = 0;
            }
        }

        // 現在処理中のブロックとフレーム種別を取得する。
        var currentBlock = status.Progress.CurrentBlockIndex;
        var frame = status.Progress.CurrentFrame;

        if (frame == CoreFrameKind.Bd && currentBlock >= 0 && currentBlock < _blockRows.Count)
        {
            if (status.ErrorRate.FrameKind == CoreFrameKind.Bd && status.ErrorRate.LatestPercent < 99.9)
            {
                SetBlockAccepted(currentBlock);
            }
            else if (!string.IsNullOrWhiteSpace(status.LastError))
            {
                SetBlockError(currentBlock, status.LastError);
            }
        }

        for (var i = 0; i < _blockRows.Count; i++)
        {
            if (_blockStates[i] == ReceiveBlockState.Accepted)
            {
                _blockRows[i].ProgressPercent = 100;
            }
            else if (status.IsRunning
                     && i == currentBlock
                     && frame is CoreFrameKind.Bh or CoreFrameKind.Bd
                     && totalBlocks > 0)
            {
                // 全体進捗をブロック局所進捗へ投影して表示する。
                var body = Math.Clamp((status.Progress.ProgressPercent - 5.0) / 90.0, 0.0, 1.0);
                var perBlock = 1.0 / totalBlocks;
                var local = totalBlocks <= 0
                    ? 0.0
                    : Math.Clamp((body - (i * perBlock)) / perBlock, 0.0, 0.99) * 100.0;
                _blockRows[i].ProgressPercent = local;

                if (status.ErrorRate.FrameKind == CoreFrameKind.Bd
                    && status.ErrorRate.LatestPercent >= 99.9
                    && !string.IsNullOrWhiteSpace(status.LastError))
                {
                    SetBlockError(i, status.LastError);
                }
            }
            else
            {
                _blockRows[i].ProgressPercent = 0;
            }
        }

        if (_totalRow is not null)
        {
            if (!status.IsRunning && !status.IsCompleted)
            {
                _totalRow.ProgressPercent = 0;
            }
            else if (status.IsCompleted && !status.IsFaulted)
            {
                _totalRow.ProgressPercent = 100;
            }
            else
            {
                _totalRow.ProgressPercent = Math.Clamp(status.Progress.ProgressPercent, 0.0, 100.0);
            }
        }

        if (!status.IsRunning && !status.IsCompleted && !string.IsNullOrWhiteSpace(status.LastError))
        {
            var index = status.Progress.CurrentBlockIndex;
            if (index >= 0 && index < _blockRows.Count)
            {
                SetBlockError(index, status.LastError);
            }
        }

        if (!string.IsNullOrWhiteSpace(status.LastError)
            && (status.LastError.StartsWith("ORPHAN ", StringComparison.Ordinal)
                || status.LastError.StartsWith("ORPHAN-RESOLVED", StringComparison.Ordinal)))
        {
            UpdateOrphanRowsFromStatus(status.LastError);
        }
    }

    internal ReceiveHistoryEntry CaptureHistoryEntry(
        IReadOnlyList<ReceiveOrphanHistory>? orphans = null,
        byte[]? payload = null)
    {
        var fileName = FindInfoValue("File Name", "(未登録データ)");
        if (string.IsNullOrWhiteSpace(fileName)
            || string.Equals(fileName, "(Not received)", StringComparison.Ordinal)
            || string.Equals(fileName, "(未受信)", StringComparison.Ordinal)
            || string.Equals(fileName, "(FH待ち)", StringComparison.Ordinal))
        {
            fileName = "(未登録データ)";
        }

        var fileSizeText = FindInfoValue("ファイルサイズ", "-");
        var blockCountText = FindInfoValue("ブロック数", "-");
        var fileSize = ParseFileSize(fileSizeText);
        var blockCount = ParseBlockCount(blockCountText, _blockStates.Length);

        var blocks = new List<ReceiveBlockHistory>(_blockStates.Length);
        for (var i = 0; i < _blockStates.Length; i++)
        {
            if (_blockStates[i] == ReceiveBlockState.Unknown)
            {
                continue;
            }

            blocks.Add(new ReceiveBlockHistory(i, _blockStates[i], _blockErrors[i]));
        }

        return new ReceiveHistoryEntry(
            EntryId: Guid.NewGuid().ToString("N"),
            Kind: HistoryEntryKind.Receive,
            ReceivedAtUtc: DateTime.UtcNow,
            ContentHashHex: ResolveReceiveHash(payload, fileName, fileSize, blockCount),
            SourcePath: _sourcePath,
            FileName: fileName,
            FileSize: fileSize,
            BlockCount: blockCount,
            IsSuccess: _lastCompletedSuccess,
            OutputPath: _lastOutputPath,
            CompletionMessage: _lastCompletionMessage,
            Payload: payload ?? Array.Empty<byte>(),
            Blocks: blocks,
            Orphans: orphans ?? Array.Empty<ReceiveOrphanHistory>());
    }

    private string ResolveReceiveHash(byte[]? payload, string fileName, long fileSize, int blockCount)
    {
        if (payload is { Length: > 0 })
        {
            return Convert.ToHexString(Hash.ComputeSha256(payload));
        }

        if (!string.IsNullOrWhiteSpace(fileName)
            && !string.Equals(fileName, "(未登録データ)", StringComparison.Ordinal)
            && fileSize > 0
            && blockCount > 0)
        {
            return $"FH:{fileName}|{fileSize}|{blockCount}";
        }

        if (!string.IsNullOrWhiteSpace(_sourcePath))
        {
            return _sourcePath;
        }

        return string.Empty;
    }

    internal void ApplyHistory(ReceiveHistoryEntry history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _sourcePath = history.SourcePath;
        _lastCompletedSuccess = history.IsSuccess;
        _lastCompletionMessage = history.CompletionMessage;
        _lastOutputPath = history.OutputPath;

        var blockCount = Math.Max(0, history.BlockCount);
        var fileSizeText = history.FileSize > 0 ? $"{history.FileSize:N0} bytes" : "-";
        EnsureRows(history.FileName, fileSizeText, blockCount.ToString(), blockCount);

        for (var i = 0; i < _blockRows.Count; i++)
        {
            _blockRows[i].ProgressPercent = 0;
            _blockRows[i].SetSeconds("-");
            _blockStates[i] = ReceiveBlockState.Unknown;
            _blockErrors[i] = string.Empty;
        }

        _orphanRowsByHash.Clear();

        foreach (var block in history.Blocks)
        {
            if (block.BlockIndex < 0 || block.BlockIndex >= _blockRows.Count)
            {
                continue;
            }

            if (block.State == ReceiveBlockState.Accepted)
            {
                SetBlockAccepted(block.BlockIndex);
                _blockRows[block.BlockIndex].ProgressPercent = 100;
            }
            else if (block.State == ReceiveBlockState.Error)
            {
                SetBlockError(block.BlockIndex, block.ErrorText);
            }
        }

        foreach (var orphan in history.Orphans)
        {
            var detail = string.IsNullOrWhiteSpace(orphan.Detail) ? "親不明" : orphan.Detail;
            AddOrUpdateOrphanRow(orphan.HashHex, $"親不明 / {detail} / {orphan.Payload.Length} bytes");
        }

        if (_fhRow is not null)
        {
            _fhRow.ProgressPercent = 100;
        }

        if (_totalRow is not null)
        {
            _totalRow.ProgressPercent = history.IsSuccess ? 100 : 0;
            _totalRow.SetSeconds(history.IsSuccess ? "Success" : "Failed");
        }
    }

    /// <summary>
    /// FH情報が揃っている状態かを判定します。
    /// </summary>
    /// <param name="status">Core 実行状態。</param>
    /// <returns>FH情報が揃っていれば true。</returns>
    public static bool HasFileHeaderInfo(CoreExecutionStatus status)
    {
        return status.Progress.TotalBlockCount > 0
               && !string.IsNullOrWhiteSpace(status.FileSizeText)
               && status.FileSizeText != "-"
               && !string.IsNullOrWhiteSpace(status.FileName)
               && status.FileName != "(未受信)"
               && status.FileName != "(FH待ち)";
    }

    private void EnsureRows(string fileName, string fileSize, string blockCountText, int totalBlocks)
    {
        if (_builtBlockCount == totalBlocks && _rows.Count > 1)
        {
            // ブロック構成が同じなら情報行のみ更新する。
            UpdateInfoValue("File Name", fileName);
            UpdateInfoValue("ファイルサイズ", fileSize);
            UpdateInfoValue("ブロック数", totalBlocks > 0 ? totalBlocks.ToString() : blockCountText);
            return;
        }

        _rows.Clear();
        _blockRows.Clear();
        _orphanRowsByHash.Clear();
        _blockStates = new ReceiveBlockState[totalBlocks];
        _blockErrors = new string[totalBlocks];
        _fhRow = null;
        _totalRow = null;
        _builtBlockCount = totalBlocks;

        _rows.Add(DetailRow.Info("File Name", fileName));
        _rows.Add(DetailRow.Info("ファイルサイズ", fileSize));
        _rows.Add(DetailRow.Info("ブロック数", totalBlocks > 0 ? totalBlocks.ToString() : blockCountText));

        _fhRow = DetailRow.Segment("FH");
        _rows.Add(_fhRow);

        for (var i = 0; i < totalBlocks; i++)
        {
            var row = DetailRow.Segment($"BLK-{i}");
            _blockRows.Add(row);
            _rows.Add(row);
        }

        _totalRow = DetailRow.Segment("Total");
        _rows.Add(_totalRow);
    }

    private void UpdateOrphanRowsFromStatus(string statusError)
    {
        var hash = ExtractHash(statusError);
        if (string.IsNullOrWhiteSpace(hash))
        {
            hash = "UNKNOWN";
        }

        var detail = statusError.StartsWith("ORPHAN-RESOLVED", StringComparison.Ordinal)
            ? "解決済み"
            : "親不明";
        AddOrUpdateOrphanRow(hash, detail + " / " + statusError);
    }

    private void AddOrUpdateOrphanRow(string hash, string detail)
    {
        var key = string.IsNullOrWhiteSpace(hash) ? "UNKNOWN" : hash;
        if (_orphanRowsByHash.TryGetValue(key, out var existing))
        {
            existing.SetSeconds(Truncate(detail, 64));
            return;
        }

        var row = DetailRow.Info($"ORPHAN-{Truncate(key, 12)}", Truncate(detail, 64));
        _orphanRowsByHash[key] = row;
        _rows.Add(row);
    }

    private static string ExtractHash(string text)
    {
        var marker = "hash=";
        var idx = text.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            return string.Empty;
        }

        var start = idx + marker.Length;
        var end = start;
        while (end < text.Length)
        {
            var c = text[end];
            if (!Uri.IsHexDigit(c))
            {
                break;
            }

            end++;
        }

        return text.Substring(start, end - start);
    }

    private void UpdateInfoValue(string name, string value)
    {
        var row = _rows.FirstOrDefault(r => r.Name == name && r.MeterVisibility == Visibility.Collapsed);
        row?.SetSeconds(value);
    }

    private string FindInfoValue(string name, string fallback)
    {
        var row = _rows.FirstOrDefault(r => r.Name == name && r.MeterVisibility == Visibility.Collapsed);
        return row?.Seconds ?? fallback;
    }

    private static long ParseFileSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var digits = new string(text.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var value) ? value : 0;
    }

    private static int ParseBlockCount(string text, int fallback)
    {
        if (int.TryParse(text, out var value) && value >= 0)
        {
            return value;
        }

        return fallback;
    }

    private void SetBlockAccepted(int index)
    {
        if (index < 0 || index >= _blockRows.Count)
        {
            return;
        }

        _blockStates[index] = ReceiveBlockState.Accepted;
        _blockErrors[index] = string.Empty;
        _blockRows[index].SetSeconds("OK");
    }

    private void SetBlockError(int index, string error)
    {
        if (index < 0 || index >= _blockRows.Count)
        {
            return;
        }

        if (_blockStates[index] == ReceiveBlockState.Accepted)
        {
            return;
        }

        _blockStates[index] = ReceiveBlockState.Error;
        _blockErrors[index] = error ?? string.Empty;
        var detail = string.IsNullOrWhiteSpace(error)
            ? "NG"
            : $"NG: {Truncate(error, 64)}";
        _blockRows[index].SetSeconds(detail);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text.Substring(0, Math.Max(0, maxLength - 3)) + "...";
    }

    /// <summary>
    /// 詳細グリッド1行分の表示モデルです。
    /// </summary>
    public sealed class DetailRow : INotifyPropertyChanged
    {
        private double _progressPercent;
        private string _seconds;

        private DetailRow(string name, string seconds, bool showMeter)
        {
            Name = name;
            _seconds = seconds;
            MeterVisibility = showMeter ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Name { get; }
        public string Seconds => _seconds;
        public Visibility MeterVisibility { get; }

        public double ProgressPercent
        {
            get => _progressPercent;
            set
            {
                var clamped = Math.Clamp(value, 0.0, 100.0);
                if (Math.Abs(_progressPercent - clamped) < 0.001)
                {
                    return;
                }

                _progressPercent = clamped;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static DetailRow Segment(string name) => new(name, "-", showMeter: true);

        public static DetailRow Info(string name, string value) => new(name, value, showMeter: false);

        public void SetSeconds(string value)
        {
            if (_seconds == value)
            {
                return;
            }

            _seconds = value;
            OnPropertyChanged(nameof(Seconds));
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}




