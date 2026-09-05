using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Onta.View;

/// <summary>
/// 受信パネルです（ファイル情報・L/R ワウフラッター・LiveCharts2 エラー率グラフ）。
/// </summary>
public partial class ReceivePanel : UserControl
{
    private readonly ErrorRateChartModel _errorChart = new();
    private readonly DispatcherTimer _demoTimer;
    private readonly Random _rng = new();
    private bool _demoRunning;
    private double _demoBias;
    private double _demoTimeSec;

    public ReceivePanel()
    {
        InitializeComponent();
        ErrorChart.DataContext = _errorChart;

        // ワウフラッター／デモは 0.2 秒間隔のスナップショット。
        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _demoTimer.Tick += OnDemoTick;

        Loaded += (_, _) =>
        {
            StartDemoFeed();
            SetFileInfo("(未受信)", "-", "-");
        };
        Unloaded += (_, _) => StopDemoFeed();
    }

    public ErrorRateChartModel ErrorGraph => _errorChart;

    public double WowFlutterLeftPercent => WowLeft.ValuePercent;

    public double WowFlutterRightPercent => WowRight.ValuePercent;

    public void SetFileInfo(string fileName, string fileSizeText, string blockCountText)
    {
        FileNameBox.Text = fileName;
        FileSizeBox.Text = fileSizeText;
        BlockCountBox.Text = blockCountText;
    }

    public void SetWowFlutterFromPilots(double leftSpeedRatio, double rightSpeedRatio)
    {
        WowLeft.SetFromSpeedRatio(leftSpeedRatio);
        WowRight.SetFromSpeedRatio(rightSpeedRatio);
    }

    public void SetWowFlutterPercent(double leftPercent, double rightPercent)
    {
        WowLeft.AddSample(leftPercent);
        WowRight.AddSample(rightPercent);
    }

    public void AddErrorRateSample(double errorRatePercent)
    {
        _errorChart.AddSample(errorRatePercent);
    }

    public void StartDemoFeed()
    {
        _demoRunning = true;
        _demoBias = 1.5;
        _demoTimeSec = 0.0;
        _demoTimer.Start();
    }

    public void StopDemoFeed()
    {
        _demoRunning = false;
        _demoTimer.Stop();
    }

    private void OnDemoTick(object? sender, EventArgs e)
    {
        if (!_demoRunning)
        {
            return;
        }

        _demoTimeSec += _demoTimer.Interval.TotalSeconds;

        _demoBias += (_rng.NextDouble() - 0.5) * 0.12;
        _demoBias = Math.Clamp(_demoBias, 0.2, 4.0);
        var spike = _rng.NextDouble() < 0.03 ? _rng.NextDouble() * 8.0 : 0.0;
        var err = Math.Max(0.0, _demoBias + ((_rng.NextDouble() - 0.5) * 0.6) + spike);
        _errorChart.AddSample(err);

        var wowL = (1.2 * Math.Sin((2.0 * Math.PI * 0.5 * _demoTimeSec) + 0.3))
                   + (0.35 * Math.Sin((2.0 * Math.PI * 6.0 * _demoTimeSec) + 1.1));
        var wowR = (1.1 * Math.Sin((2.0 * Math.PI * 0.5 * _demoTimeSec) + 2.2))
                   + (0.40 * Math.Sin((2.0 * Math.PI * 6.0 * _demoTimeSec) + 0.4));
        wowL += (_rng.NextDouble() - 0.5) * 0.08;
        wowR += (_rng.NextDouble() - 0.5) * 0.08;
        SetWowFlutterPercent(wowL, wowR);
    }
}
