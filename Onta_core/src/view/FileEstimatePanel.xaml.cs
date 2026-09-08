using System.Collections.ObjectModel;
using System.Windows.Controls;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// ファイル見積パネルです（内容／秒数／メーター）。
/// </summary>
public partial class FileEstimatePanel : UserControl
{
    private const int DataBlockBytes = 4096;
    private readonly ObservableCollection<EstimateRow> _rows = [];

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

        if (fileSizeBytes <= 0)
        {
            _rows.Add(new EstimateRow("入力ファイル", "未選択", ""));
            return;
        }

        var profile = new FileWavCodecProfile(
            ActiveSubcarriers: settings.ActiveSubcarriers,
            ModulationScheme: settings.ModulationScheme,
            ChannelMode: settings.ChannelMode,
            BlockInterleaveFactor: settings.BlockInterleaveFactor);

        var estimate = FileWavCodec.EstimateTransmissionDuration(profile, fileSizeBytes);
        var maxSeconds = estimate.Segments.Count == 0
            ? 0.001
            : Math.Max(0.001, estimate.Segments.Max(s => s.Seconds));

        foreach (var seg in estimate.Segments)
        {
            _rows.Add(CreateRow(seg.Label, seg.Seconds, maxSeconds));
        }

        var blockCount = Math.Max(1, (int)((fileSizeBytes + DataBlockBytes - 1) / DataBlockBytes));
        _rows.Add(new EstimateRow("入力サイズ", $"{fileSizeBytes:N0} bytes", ""));
        _rows.Add(new EstimateRow("ブロック数", blockCount.ToString(), ""));
        _rows.Add(CreateRow("合計", estimate.TotalSeconds, Math.Max(maxSeconds, estimate.TotalSeconds)));
    }

    private static long ResolveInputSize(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            return 0;
        }

        return new FileInfo(inputPath).Length;
    }

    private static EstimateRow CreateRow(string name, double seconds, double maxSeconds)
    {
        var clampedMax = Math.Max(0.001, maxSeconds);
        var filled = Math.Clamp((int)Math.Round(24.0 * seconds / clampedMax), 1, 24);
        return new EstimateRow(name, seconds.ToString("0.###"), new string('■', filled));
    }

    private sealed record EstimateRow(string Name, string Seconds, string Meter);
}
