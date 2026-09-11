using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// 送信設定から伝送時間見積りを表示するパネルです。
/// </summary>
public partial class FileEstimatePanel : UserControl
{
    private const int DataBlockBytes = 4096;
    private readonly ObservableCollection<EstimateRow> _rows = [];
    private readonly List<SegmentTiming> _segmentTimings = [];
    private double _totalSeconds;

    /// <summary>
    /// 見積りパネルを初期化します。
    /// </summary>
    public FileEstimatePanel()
    {
        InitializeComponent();
        EstimateGrid.ItemsSource = _rows;
    }

    /// <summary>
    /// 現在の送信設定に基づいて見積り行を再構築します。
    /// </summary>
    /// <param name="settings">送信設定スナップショット。</param>
    public void UpdateEstimate(SendSettingsSnapshot settings)
    {
        var fileSizeBytes = ResolveInputSize(settings.InputFilePath);
        _rows.Clear();
        _segmentTimings.Clear();
        _totalSeconds = 0;

        if (fileSizeBytes <= 0)
        {
            _rows.Add(EstimateRow.Info("Input file", "Not selected"));
            return;
        }

        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: settings.ActiveSubcarriers,
            ModulationScheme: settings.ModulationScheme,
            ChannelMode: settings.ChannelMode,
            BlockInterleaveFactor: settings.BlockInterleaveFactor);

        var estimate = FileWavCodec.EstimateTransmissionDuration(profile, fileSizeBytes);
        _totalSeconds = Math.Max(0.0, estimate.TotalSeconds);
        // 先頭無音分を起点として、各セグメントの開始・終了秒を積算する。
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
        _rows.Add(EstimateRow.Info("ファイルサイズ", $"{fileSizeBytes:N0} bytes"));
        _rows.Add(EstimateRow.Info("ブロック数", blockCount.ToString()));
        _rows.Add(EstimateRow.Segment("Total", estimate.TotalSeconds));
    }

    /// <summary>
    /// 経過時間に応じて各セグメントの進捗率を更新します。
    /// </summary>
    /// <param name="elapsedSeconds">送信開始からの経過秒。</param>
    /// <param name="isRunning">送信中かどうか。</param>
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

        // Total 行は全体進捗として別計算する。
        var totalRow = _rows.LastOrDefault(r => r.Name == "Total");
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

    /// <summary>
    /// 進捗表示を初期状態に戻します。
    /// </summary>
    public void ResetProgress()
    {
        ApplyProgress(0, isRunning: false);
    }

    /// <summary>
    /// 指定区間に対する経過進捗率（0-100）を計算します。
    /// </summary>
    /// <param name="elapsed">全体の経過秒。</param>
    /// <param name="start">区間開始秒。</param>
    /// <param name="end">区間終了秒。</param>
    /// <returns>区間進捗率（0-100）。</returns>
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

    /// <summary>
    /// 入力ファイルのサイズを返します。未選択または未存在時は 0 を返します。
    /// </summary>
    /// <param name="inputPath">入力ファイルパス。</param>
    /// <returns>ファイルサイズ（bytes）。</returns>
    private static long ResolveInputSize(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return 0;
        }

        return new FileInfo(inputPath).Length;
    }

    /// <summary>
    /// セグメント行とその時間範囲を関連付ける内部データです。
    /// </summary>
    private readonly record struct SegmentTiming(EstimateRow Row, double StartSeconds, double EndSeconds);

    /// <summary>
    /// 見積りグリッド 1 行分の表示モデルです。
    /// </summary>
    public sealed class EstimateRow : INotifyPropertyChanged
    {
        private double _progressPercent;

        /// <summary>
        /// 見積り行を生成します。
        /// </summary>
        /// <param name="name">項目名。</param>
        /// <param name="seconds">表示値文字列。</param>
        /// <param name="showMeter">メーター表示有無。</param>
        private EstimateRow(string name, string seconds, bool showMeter)
        {
            Name = name;
            Seconds = seconds;
            MeterVisibility = showMeter ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// 項目名です。
        /// </summary>
        public string Name { get; }
        /// <summary>
        /// 表示値文字列です。
        /// </summary>
        public string Seconds { get; }
        /// <summary>
        /// メーター表示有無です。
        /// </summary>
        public Visibility MeterVisibility { get; }

        /// <summary>
        /// メーター進捗率（0-100）です。
        /// </summary>
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

        /// <summary>
        /// 表示更新通知イベントです。
        /// </summary>
        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// メーター付きのセグメント行を作成します。
        /// </summary>
        /// <param name="name">セグメント名。</param>
        /// <param name="seconds">セグメント秒数。</param>
        /// <returns>セグメント行。</returns>
        public static EstimateRow Segment(string name, double seconds) =>
            new(name, Math.Round(seconds, 1, MidpointRounding.AwayFromZero).ToString("0.0"), showMeter: true);

        /// <summary>
        /// メーターなしの情報行を作成します。
        /// </summary>
        /// <param name="name">項目名。</param>
        /// <param name="seconds">表示値文字列。</param>
        /// <returns>情報行。</returns>
        public static EstimateRow Info(string name, string seconds) =>
            new(name, seconds, showMeter: false);

        /// <summary>
        /// プロパティ変更通知を発火します。
        /// </summary>
        /// <param name="propertyName">変更されたプロパティ名。</param>
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}




