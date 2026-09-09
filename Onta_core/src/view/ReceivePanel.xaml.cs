using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// 受信パネルです（ファイル情報・L/R ワウフラッター・エラー率 / I-Q グラフ）。
/// </summary>
public partial class ReceivePanel : UserControl
{
    private readonly ErrorRateChartModel _errorChart = new();
    private readonly FftChartModel _fftChart = new();
    private readonly IqChartModel _iqChart = new();
    private readonly DispatcherTimer _demoTimer;
    private readonly Random _rng = new();
    private bool _demoRunning;
    private double _demoBias;
    private double _demoTimeSec;
    private string _receiveSource = "WAV入力";
    private string? _selectedWavPath;
    private CoreFrameKind _lastErrorFrame = CoreFrameKind.Fh;
    private double _lastErrorPercent = -1;

    public ReceivePanel()
    {
        InitializeComponent();
        ErrorChart.DataContext = _errorChart;
        FftChart.DataContext = _fftChart;
        IqChart.DataContext = _iqChart;

        // ワウフラッター／デモは 0.2 秒間隔のスナップショット。
        _demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _demoTimer.Tick += OnDemoTick;

        Loaded += (_, _) =>
        {
            StopDemoFeed();
            SetWowFlutterPercent(0, 0);
            ErrorGraph.Clear();
            _fftChart.Clear();
            _iqChart.Clear();
            SetFileInfo("(未受信)", "-", "-");
            ProgressBox.Text = "-";
        };
        Unloaded += (_, _) => StopDemoFeed();
    }

    public event EventHandler? ReceiveStartRequested;

    public string ReceiveSource => _receiveSource;

    public string? SelectedWavPath => _selectedWavPath;

    public ErrorRateChartModel ErrorGraph => _errorChart;

    public FftChartModel FftGraph => _fftChart;

    public IqChartModel IqGraph => _iqChart;

    public double WowFlutterLeftPercent => WowLeft.ValuePercent;

    public double WowFlutterRightPercent => WowRight.ValuePercent;

    public void SetFileInfo(string fileName, string fileSizeText, string blockCountText)
    {
        FileNameBox.Text = fileName;
        FileSizeBox.Text = fileSizeText;
        BlockCountBox.Text = blockCountText;
    }

    /// <summary>
    /// コア問い合わせ結果（進捗 / エラー率 / I-Q）を画面へ反映します。
    /// </summary>
    public void ApplyExecutionStatus(CoreExecutionStatus status)
    {
        SetFileInfo(status.FileName, status.FileSizeText, status.BlockCountText);

        var frameLabel = status.Progress.CurrentFrame switch
        {
            CoreFrameKind.Fh => "FH",
            CoreFrameKind.Bh => $"BH#{Math.Max(0, status.Progress.CurrentBlockIndex)}",
            _ => $"BD#{Math.Max(0, status.Progress.CurrentBlockIndex)}"
        };
        ProgressBox.Text =
            $"{frameLabel} {status.Progress.ProgressPercent:0.0}% " +
            $"({status.Progress.AcceptedBlockCount}/{Math.Max(status.Progress.TotalBlockCount, 0)})";

        SetWowFlutterPercent(status.WowLeftPercent, status.WowRightPercent);

        var err = status.ErrorRate.LatestPercent;
        var errFrame = status.ErrorRate.FrameKind;
        if (Math.Abs(err - _lastErrorPercent) > 1e-6 || errFrame != _lastErrorFrame)
        {
            _lastErrorPercent = err;
            _lastErrorFrame = errFrame;
            _errorChart.AddSample(err, errFrame switch
            {
                CoreFrameKind.Fh => ErrorRateFrameKind.Fh,
                CoreFrameKind.Bh => ErrorRateFrameKind.Bh,
                _ => ErrorRateFrameKind.Bd
            });
        }

        _iqChart.ReplacePoints(status.IqGraph.Points);
        if (status.IqGraph.ActiveSubcarrierCount > 0)
        {
            IqTitle.Text = $"I-Q ({status.IqGraph.ModulationScheme} / SC={status.IqGraph.ActiveSubcarrierCount})";
        }
        else
        {
            IqTitle.Text = "I-Q";
        }

        _fftChart.ReplacePoints(
            status.FftGraph.LeftPoints,
            status.FftGraph.RightPoints,
            status.FftGraph.IsStereo);
        if (status.FftGraph.FftSize > 0)
        {
            var mode = status.FftGraph.IsStereo ? "Stereo" : "Mono";
            FftTitle.Text = $"FFT ({mode} / N={status.FftGraph.FftSize})";
        }
        else
        {
            FftTitle.Text = "FFT";
        }
    }

    /// <summary>
    /// 送信動作中など、受信変調に基づく表示ができないときは I-Q を空にします。
    /// </summary>
    public void ClearIqDisplay()
    {
        _iqChart.Clear();
        IqTitle.Text = "I-Q";
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
        _selectedWavPath = dlg.FileName;
        var fileInfo = new FileInfo(dlg.FileName);
        SetFileInfo(fileInfo.Name, $"{fileInfo.Length:N0} bytes", "-");
        ProgressBox.Text = "-";
    }

    private void OnUseAudioInput(object sender, RoutedEventArgs e)
    {
        _receiveSource = "音声入力";
        _selectedWavPath = null;
        SetFileInfo("(音声入力)", "-", "-");
        ProgressBox.Text = "-";
    }

    private void OnReceiveStartClick(object sender, RoutedEventArgs e)
    {
        ReceiveStartRequested?.Invoke(this, EventArgs.Empty);
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
