using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Onta.View.Performance;

public partial class PerformancePanel : UserControl
{
    private readonly DispatcherTimer _demoTimer;
    private readonly Random _random = new();
    private readonly List<double> _wowLHistory = [];
    private readonly List<double> _wowRHistory = [];
    private readonly List<Rectangle> _fftBars = [];
    private readonly List<Ellipse> _iqDots = [];
    private bool _isRunning;

    public PerformancePanel()
    {
        InitializeComponent();

        _demoTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        _demoTimer.Tick += OnDemoTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WowLeftMeter.ChannelLabel = "L";
        WowRightMeter.ChannelLabel = "R";
        WowLeftMeter.RangePercent = 1.0;
        WowRightMeter.RangePercent = 1.0;

        if (AudioDeviceCombo.Items.Count == 0)
        {
            AudioDeviceCombo.Items.Add("既定デバイス");
            AudioDeviceCombo.Items.Add("Device 1");
            AudioDeviceCombo.Items.Add("Device 2");
            AudioDeviceCombo.SelectedIndex = 0;
        }

        EnsureIqDots();
        RedrawWowTrend();
        DrawFftBars();
        RedrawIqScatter();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _demoTimer.Stop();
        _isRunning = false;
        StartButton.Content = "スタート";
        StatusText.Text = "停止";
    }

    private void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "測定用ファイルを選択",
            Filter = "すべてのファイル (*.*)|*.*"
        };

        if (dlg.ShowDialog() == true)
        {
            InputFilePathTextBox.Text = dlg.FileName;
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _demoTimer.Stop();
            _isRunning = false;
            StartButton.Content = "スタート";
            StatusText.Text = "停止";
            return;
        }

        _demoTimer.Start();
        _isRunning = true;
        StartButton.Content = "停止";
        StatusText.Text = "計測中";
    }

    private void OnWowTrendCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        RedrawWowTrend();
    }

    private void OnFftCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _fftBars.Clear();
        FftCanvas.Children.Clear();
        BuildFftBars();
        DrawFftBars();
    }

    private void OnDemoTick(object? sender, EventArgs e)
    {
        var l = NextWowValue();
        var r = NextWowValue();

        WowLeftMeter.AddSample(l);
        WowRightMeter.AddSample(r);
        WowLValueText.Text = $"{l:+0.00;-0.00;0.00}%";
        WowRValueText.Text = $"{r:+0.00;-0.00;0.00}%";

        PushWowSample(_wowLHistory, l);
        PushWowSample(_wowRHistory, r);
        RedrawWowTrend();

        DrawFftBars();
        RedrawIqScatter();
    }

    private double NextWowValue()
    {
        return (_random.NextDouble() - 0.5) * 1.2;
    }

    private static void PushWowSample(List<double> history, double value)
    {
        history.Add(value);
        const int maxPoints = 160;
        if (history.Count > maxPoints)
        {
            history.RemoveAt(0);
        }
    }

    private void RedrawWowTrend()
    {
        DrawWowPolyline(WowLTrendLine, _wowLHistory, WowTrendCanvas.ActualWidth, WowTrendCanvas.ActualHeight, 1.0);
        DrawWowPolyline(WowRTrendLine, _wowRHistory, WowTrendCanvas.ActualWidth, WowTrendCanvas.ActualHeight, 1.0);
    }

    private static void DrawWowPolyline(Polyline line, List<double> source, double width, double height, double range)
    {
        line.Points.Clear();
        if (source.Count < 2 || width <= 2 || height <= 2)
        {
            return;
        }

        var yCenter = height / 2.0;
        var plotHeight = Math.Max(1.0, height - 6.0);
        var xStep = width / Math.Max(1, source.Count - 1);

        for (var i = 0; i < source.Count; i++)
        {
            var x = i * xStep;
            var ratio = Math.Clamp(source[i] / range, -1.0, 1.0);
            var y = yCenter - ratio * (plotHeight / 2.0);
            line.Points.Add(new Point(x, y));
        }
    }

    private void BuildFftBars()
    {
        if (FftCanvas.ActualWidth <= 2 || FftCanvas.ActualHeight <= 2)
        {
            return;
        }

        if (_fftBars.Count > 0)
        {
            return;
        }

        const int barCount = 56;
        var w = FftCanvas.ActualWidth;
        var spacing = 2.0;
        var barWidth = Math.Max(1.0, (w - ((barCount + 1) * spacing)) / barCount);

        for (var i = 0; i < barCount; i++)
        {
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = 1,
                RadiusX = 1,
                RadiusY = 1,
                Fill = i % 2 == 0
                    ? new SolidColorBrush(Color.FromRgb(108, 191, 106))
                    : new SolidColorBrush(Color.FromRgb(212, 160, 90))
            };

            Canvas.SetLeft(bar, spacing + i * (barWidth + spacing));
            Canvas.SetTop(bar, FftCanvas.ActualHeight - 1);
            _fftBars.Add(bar);
            FftCanvas.Children.Add(bar);
        }
    }

    private void DrawFftBars()
    {
        if (FftCanvas.ActualWidth <= 2 || FftCanvas.ActualHeight <= 2)
        {
            return;
        }

        if (_fftBars.Count == 0)
        {
            BuildFftBars();
        }

        var h = FftCanvas.ActualHeight;
        for (var i = 0; i < _fftBars.Count; i++)
        {
            var bar = _fftBars[i];
            var norm = 0.15 + 0.75 * _random.NextDouble();
            var height = Math.Max(2.0, norm * h);
            bar.Height = height;
            Canvas.SetTop(bar, h - height);
        }
    }

    private void EnsureIqDots()
    {
        if (_iqDots.Count > 0)
        {
            return;
        }

        IqCanvas.Children.Clear();

        var centerX = 160.0;
        var centerY = 160.0;
        var axisBrush = new SolidColorBrush(Color.FromArgb(140, 176, 181, 191));

        var xAxis = new Line
        {
            X1 = 20,
            Y1 = centerY,
            X2 = 300,
            Y2 = centerY,
            Stroke = axisBrush,
            StrokeThickness = 1
        };
        var yAxis = new Line
        {
            X1 = centerX,
            Y1 = 20,
            X2 = centerX,
            Y2 = 300,
            Stroke = axisBrush,
            StrokeThickness = 1
        };

        IqCanvas.Children.Add(xAxis);
        IqCanvas.Children.Add(yAxis);

        Brush[] brushes =
        [
            new SolidColorBrush(Color.FromRgb(78, 140, 245)),
            new SolidColorBrush(Color.FromRgb(108, 191, 106)),
            new SolidColorBrush(Color.FromRgb(232, 234, 237)),
            new SolidColorBrush(Color.FromRgb(212, 160, 90)),
            new SolidColorBrush(Color.FromRgb(165, 116, 72)),
            new SolidColorBrush(Color.FromRgb(147, 101, 214))
        ];

        const int dotCount = 64;
        for (var i = 0; i < dotCount; i++)
        {
            var dot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Fill = brushes[i % brushes.Length],
                Opacity = 0.95
            };

            _iqDots.Add(dot);
            IqCanvas.Children.Add(dot);
        }
    }

    private void RedrawIqScatter()
    {
        if (_iqDots.Count == 0)
        {
            EnsureIqDots();
        }

        const double center = 160.0;
        const double maxRadius = 118.0;

        for (var i = 0; i < _iqDots.Count; i++)
        {
            var dot = _iqDots[i];
            var angle = _random.NextDouble() * Math.PI * 2.0;
            var radius = maxRadius * Math.Sqrt(_random.NextDouble());
            var x = center + Math.Cos(angle) * radius;
            var y = center + Math.Sin(angle) * radius;

            Canvas.SetLeft(dot, x - (dot.Width / 2.0));
            Canvas.SetTop(dot, y - (dot.Height / 2.0));
        }
    }
}
