using System.Collections.ObjectModel;
using System.Windows.Controls;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// ファイル見積パネルです（内容／秒数／メーター）。
/// </summary>
public partial class FileEstimatePanel : UserControl
{
    private readonly ObservableCollection<EstimateRow> _rows = [];

    public FileEstimatePanel()
    {
        InitializeComponent();
        EstimateGrid.ItemsSource = _rows;
    }

    /// <summary>
    /// 送信設定から見積行を更新します（プレースホルダ値）。
    /// </summary>
    public void UpdateEstimate(SendSettingsSnapshot settings)
    {
        var rateFactor = settings.ActiveSubcarriers switch
        {
            9 => 1.4,
            18 => 1.0,
            27 => 0.8,
            36 => 0.65,
            _ => 1.0
        };
        rateFactor *= settings.ModulationScheme switch
        {
            ModulationScheme.Bpsk => 1.6,
            ModulationScheme.Qpsk => 1.0,
            ModulationScheme.Qam16 => 0.7,
            ModulationScheme.Qam64 => 0.5,
            _ => 1.0
        };

        var headerSec = Math.Max(1, (int)Math.Round(10 * rateFactor));
        var blockSec = Math.Max(1, (int)Math.Round(9 * rateFactor));
        var blockCount = 4;
        var interleave = Math.Clamp(settings.BlockInterleaveFactor, 1, 3);
        var total = (headerSec * (interleave + 1)) + (blockSec * blockCount * interleave);
        var maxBar = Math.Max(1, total);

        _rows.Clear();
        _rows.Add(CreateRow("ヘッダー", headerSec * (interleave + 1), maxBar));
        for (var i = 1; i <= blockCount; i++)
        {
            var name = interleave <= 1 ? $"ブロック{i}" : $"ブロック{i}（{interleave}回）";
            _rows.Add(CreateRow(name, blockSec * interleave, maxBar));
        }

        _rows.Add(CreateRow("合計", total, maxBar));
    }

    private static EstimateRow CreateRow(string name, int seconds, int maxBar)
    {
        var filled = Math.Clamp((int)Math.Round(24.0 * seconds / maxBar), 1, 24);
        var percent = Math.Clamp((int)Math.Round(100.0 * seconds / maxBar), 1, 100);
        return new EstimateRow(name, seconds.ToString(), $"{new string('■', filled)} {percent}%");
    }

    private sealed record EstimateRow(string Name, string Seconds, string Meter);
}
