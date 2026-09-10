using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// 受信詳細パネルです（送信詳細と同じ 内容／秒数／メーター レイアウト）。
/// FH 受信後にファイル情報とブロック別メーターを表示します。
/// </summary>
public partial class ReceiveDetailPanel : UserControl
{
    private readonly ObservableCollection<DetailRow> _rows = [];
    private DetailRow? _fhRow;
    private DetailRow? _totalRow;
    private readonly List<DetailRow> _blockRows = [];
    private int _builtBlockCount = -1;

    public ReceiveDetailPanel()
    {
        InitializeComponent();
        DetailGrid.ItemsSource = _rows;
        Clear();
    }

    /// <summary>表示を未受信状態へ戻します。</summary>
    public void Clear()
    {
        _rows.Clear();
        _blockRows.Clear();
        _fhRow = null;
        _totalRow = null;
        _builtBlockCount = -1;
        _rows.Add(DetailRow.Info("ファイル名", "(未受信)"));
    }

    /// <summary>FH 確定時点のファイル情報で行を組み立てます（ポーリング待ちなし）。</summary>
    public void ApplyFileHeader(string fileName, string fileSizeText, int blockCount)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "(無名)" : fileName;
        var size = string.IsNullOrWhiteSpace(fileSizeText) ? "-" : fileSizeText;
        var blocks = Math.Max(0, blockCount);
        EnsureRows(name, size, blocks > 0 ? blocks.ToString() : "-", blocks);
        if (_fhRow is not null)
        {
            _fhRow.ProgressPercent = 100;
        }
    }

    /// <summary>コア実行状況から詳細表示を更新します。</summary>
    public void ApplyStatus(CoreExecutionStatus status)
    {
        // FH 未確定のあいだはプレースホルダのまま（途中の "(未受信)" で行を再構築しない）。
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
            ? "(無名)"
            : status.FileName;
        var fileSize = string.IsNullOrWhiteSpace(status.FileSizeText) ? "-" : status.FileSizeText;
        var blockCountText = string.IsNullOrWhiteSpace(status.BlockCountText) ? "-" : status.BlockCountText;
        var totalBlocks = Math.Max(0, status.Progress.TotalBlockCount);

        EnsureRows(fileName, fileSize, blockCountText, totalBlocks);

        // FH: ヘッダー確定後は満了、それ以前はコア進捗を反映。
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

        // ブロック行: 受理済みは 100%、処理中ブロックは部分進捗、未到達は 0。
        var accepted = Math.Max(0, status.Progress.AcceptedBlockCount);
        var currentBlock = status.Progress.CurrentBlockIndex;
        var frame = status.Progress.CurrentFrame;
        for (var i = 0; i < _blockRows.Count; i++)
        {
            if (i < accepted)
            {
                _blockRows[i].ProgressPercent = 100;
            }
            else if (status.IsRunning
                     && i == currentBlock
                     && frame is CoreFrameKind.Bh or CoreFrameKind.Bd
                     && totalBlocks > 0)
            {
                // FH 分を除いた残り進捗を現在ブロックへ割り当てる概算。
                var body = Math.Clamp((status.Progress.ProgressPercent - 5.0) / 90.0, 0.0, 1.0);
                var perBlock = 1.0 / totalBlocks;
                var local = totalBlocks <= 0
                    ? 0.0
                    : Math.Clamp((body - (i * perBlock)) / perBlock, 0.0, 0.99) * 100.0;
                _blockRows[i].ProgressPercent = local;
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
    }

    /// <summary>FH 由来のファイル情報が揃っているか（この時点で画面へ出してよい）。</summary>
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
            // 情報行だけ値更新（行再構築しない）。
            UpdateInfoValue("ファイル名", fileName);
            UpdateInfoValue("ファイルサイズ", fileSize);
            UpdateInfoValue("ブロック数", totalBlocks > 0 ? totalBlocks.ToString() : blockCountText);
            return;
        }

        _rows.Clear();
        _blockRows.Clear();
        _fhRow = null;
        _totalRow = null;
        _builtBlockCount = totalBlocks;

        _rows.Add(DetailRow.Info("ファイル名", fileName));
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

        _totalRow = DetailRow.Segment("合計");
        _rows.Add(_totalRow);
    }

    private void UpdateInfoValue(string name, string value)
    {
        var row = _rows.FirstOrDefault(r => r.Name == name && r.MeterVisibility == Visibility.Collapsed);
        row?.SetSeconds(value);
    }

    /// <summary>受信詳細グリッド行（送信詳細の EstimateRow と同型）。</summary>
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
