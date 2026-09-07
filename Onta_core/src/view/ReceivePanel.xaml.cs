using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

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
    private string _receiveSource = "WAV入力";

    public ReceivePanel()
    {
        InitializeComponent();
        ErrorChart.DataContext = _errorChart;

        // ワウフラッター／デモは 0.2 秒間隔のスナップショット。
        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _demoTimer.Tick += OnDemoTick;

        Loaded += (_, _) =>
        {
            StopDemoFeed();
            SetWowFlutterPercent(0, 0);
            ErrorGraph.Clear();
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

    public void AddErrorRateSample(double errorRatePercent, ErrorRateFrameKind frameKind)
    {
        _errorChart.AddSample(errorRatePercent, frameKind);
    }

    public void AddErrorRateSamples(double errorRatePercent, IReadOnlyList<ErrorRateFrameKind> frameKinds)
    {
        if (frameKinds.Count == 0)
        {
            return;
        }

        for (var i = 0; i < frameKinds.Count; i++)
        {
            _errorChart.AddSample(errorRatePercent, frameKinds[i]);
        }
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

    private void OnBrowseWavInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "受信 WAV ファイルを選択",
            Filter = "WAV (*.wav)|*.wav|すべてのファイル (*.*)|*.*",
            InitialDirectory = AppPaths.InputDir
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        _receiveSource = "WAV入力";
        var fileInfo = new FileInfo(dlg.FileName);
        SetFileInfo(fileInfo.Name, $"{fileInfo.Length:N0} bytes", "-");
    }

    private void OnUseAudioInput(object sender, RoutedEventArgs e)
    {
        _receiveSource = "音声入力";
        SetFileInfo("(音声入力)", "-", "-");
    }

    private void OnReceiveStartClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show($"受信スタート（たたき台）\n入力元: {_receiveSource}", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
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
        _errorChart.AddSample(err, ErrorRateFrameKind.Bd);

        var wowL = (1.2 * Math.Sin((2.0 * Math.PI * 0.5 * _demoTimeSec) + 0.3))
                   + (0.35 * Math.Sin((2.0 * Math.PI * 6.0 * _demoTimeSec) + 1.1));
        var wowR = (1.1 * Math.Sin((2.0 * Math.PI * 0.5 * _demoTimeSec) + 2.2))
                   + (0.40 * Math.Sin((2.0 * Math.PI * 6.0 * _demoTimeSec) + 0.4));
        wowL += (_rng.NextDouble() - 0.5) * 0.08;
        wowR += (_rng.NextDouble() - 0.5) * 0.08;
        SetWowFlutterPercent(wowL, wowR);
    }
}
