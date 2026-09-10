using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// ファイル見積パネルです（内容／秒数／グラフィックメーター）。
/// 送信中は 0.5 秒ポーリングでメーター進捗を更新します。
/// </summary>
public partial class FileEstimatePanel : UserControl
{
    private const int DataBlockBytes = 4096;
    private readonly ObservableCollection<EstimateRow> _rows = [];
    private readonly List<SegmentTiming> _segmentTimings = [];
    private double _totalSeconds;

    public FileEstimatePanel()
    {
        InitializeComponent();
        EstimateGrid.ItemsSource = _rows;
    }

    /// <summary>
    /// 送信設定から見積行を更新します（コア見積 API を利用）。
    /// </summary>
    public void UpdateEstimate(SendSettingsSnapshot settings)
    {
        var fileSizeBytes = ResolveInputSize(settings.InputFilePath);
        _rows.Clear();
        _segmentTimings.Clear();
        _totalSeconds = 0;

        if (fileSizeBytes <= 0)
        {
            _rows.Add(EstimateRow.Info("入力ファイル", "未選択"));
            return;
        }

        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: settings.ActiveSubcarriers,
            ModulationScheme: settings.ModulationScheme,
            ChannelMode: settings.ChannelMode,
            BlockInterleaveFactor: settings.BlockInterleaveFactor);

        var estimate = FileWavCodec.EstimateTransmissionDuration(profile, fileSizeBytes);
        _totalSeconds = Math.Max(0.0, estimate.TotalSeconds);
        // LEAD（先頭無音）は行に出さないが、実 PCM では先行するため開始時刻をずらす。
        var cursor = profile.LeadingSilenceSamples / (double)Math.Max(1, profile.SampleRate);

        foreach (var seg in estimate.Segments)
        {
            var start = cursor;
            var end = cursor + Math.Max(0.0, seg.Seconds);
            cursor = end;
            var row = EstimateRow.Segment(seg.Label, seg.Seconds);
            _rows.Add(row);
            _segmentTimings.Add(new SegmentTiming(row, start, end));
        }

        var blockCount = Math.Max(1, (int)((fileSizeBytes + DataBlockBytes - 1) / DataBlockBytes));
        _rows.Add(EstimateRow.Info("入力サイズ", $"{fileSizeBytes:N0} bytes"));
        _rows.Add(EstimateRow.Info("ブロック数", blockCount.ToString()));
        _rows.Add(EstimateRow.Segment("合計", estimate.TotalSeconds));
    }

    /// <summary>
    /// 送信進捗（経過秒）に応じて各行メーターを更新します。
    /// </summary>
    /// <param name="elapsedSeconds">再生／符号化の経過秒（0 以上）。</param>
    /// <param name="isRunning">送信コア実行中か。</param>
    public void ApplyProgress(double elapsedSeconds, bool isRunning)
    {
        if (_segmentTimings.Count == 0)
        {
            return;
        }

        var elapsed = Math.Max(0.0, elapsedSeconds);
        foreach (var timing in _segmentTimings)
        {
            timing.Row.ProgressPercent = ResolveSegmentProgress(elapsed, timing.StartSeconds, timing.EndSeconds);
        }

        // 合計行は全体進捗。
        var totalRow = _rows.LastOrDefault(r => r.Name == "合計");
        if (totalRow is not null)
        {
            if (!isRunning && elapsed <= 0.0)
            {
                totalRow.ProgressPercent = 0;
            }
            else if (_totalSeconds <= 0.0)
            {
                totalRow.ProgressPercent = isRunning ? 0 : 100;
            }
            else
            {
                totalRow.ProgressPercent = Math.Clamp(100.0 * elapsed / _totalSeconds, 0.0, 100.0);
            }
        }
    }

    /// <summary>進捗メーターをすべて 0 に戻します。</summary>
    public void ResetProgress()
    {
        ApplyProgress(0, isRunning: false);
    }

    private static double ResolveSegmentProgress(double elapsed, double start, double end)
    {
        if (elapsed <= start)
        {
            return 0;
        }

        if (elapsed >= end)
        {
            return 100;
        }

        var duration = end - start;
        if (duration <= 0.0)
        {
            return 100;
        }

        return Math.Clamp(100.0 * (elapsed - start) / duration, 0.0, 100.0);
    }

    private static long ResolveInputSize(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return 0;
        }

        return new FileInfo(inputPath).Length;
    }

    private readonly record struct SegmentTiming(EstimateRow Row, double StartSeconds, double EndSeconds);

    /// <summary>見積グリッド行（INotify でメーターをリアルタイム更新）。</summary>
    public sealed class EstimateRow : INotifyPropertyChanged
    {
        private double _progressPercent;

        private EstimateRow(string name, string seconds, bool showMeter)
        {
            Name = name;
            Seconds = seconds;
            MeterVisibility = showMeter ? Visibility.Visible : Visibility.Collapsed;
        }

        public string Name { get; }
        public string Seconds { get; }
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

        public static EstimateRow Segment(string name, double seconds) =>
            new(name, Math.Round(seconds, 1, MidpointRounding.AwayFromZero).ToString("0.0"), showMeter: true);

        public static EstimateRow Info(string name, string seconds) =>
            new(name, seconds, showMeter: false);

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
