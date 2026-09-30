using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.Wave;
using Onta.Core;
using Onta.Stream;
using Onta.View.Core;
using Onta.View.Language;

namespace Onta.View.Stream;

/// <summary>
/// ストリーム録音・再生パネルです。
/// </summary>
public partial class StreamPanel : UserControl
{
    private readonly StreamTxWorker _tx = new();
    private readonly StreamRxWorker _rx = new();
    private readonly DispatcherTimer _pollTimer;
    private bool _runningNotified;

    /// <summary>送信または受信が実行中なら true。</summary>
    public bool IsRunning => _tx.IsBusy || _rx.IsBusy;

    /// <summary>実行中状態が変わったときに通知します。</summary>
    public event EventHandler? RunningStateChanged;
    private readonly ErrorRateChartModel _errorChart = new();
    private readonly FftChartModel _fftChart = new();
    private readonly IqChartModel _iqChart = new();
    private string _txWavPath = string.Empty;
    private string _coverPath = string.Empty;
    private StreamCoverFormat _coverFormat = StreamCoverFormat.Color32;
    private int _coverByteCount;
    private int _lastRxCoverBlocks = -1;
    private int _lastRxCoverTotal = -1;
    private byte[]? _lastRxCover;

    /// <summary>
    /// パネルを初期化します。
    /// </summary>
    public StreamPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _pollTimer.Tick += OnPollTick;
    }

    /// <summary>
    /// デバイス一覧とグラフを初期化し、状態の定期更新を始めます。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FillDevices(TxInputDeviceBox, isInput: true);
        FillDevices(TxOutputDeviceBox, isInput: false);
        FillDevices(RxInputDeviceBox, isInput: true);
        FillDevices(RxOutputDeviceBox, isInput: false);
        UpdateTxInputModeUi();
        RxErrorChart.Series = _errorChart.Series;
        RxErrorChart.XAxes = _errorChart.XAxes;
        RxErrorChart.YAxes = _errorChart.YAxes;
        RxFftChart.Series = _fftChart.Series;
        RxFftChart.XAxes = _fftChart.XAxes;
        RxFftChart.YAxes = _fftChart.YAxes;
        RxFftChart.DrawMargin = FftChartModel.CreateDrawMarginWithFrequencyLabels();
        RxFftChart.ClipToBounds = false;
        _fftChart.ShowStereoChannels();
        RxIqChart.Series = _iqChart.Series;
        RxIqChart.XAxes = _iqChart.XAxes;
        RxIqChart.YAxes = _iqChart.YAxes;
        BuildStreamIqGroupLegend();
        UpdateStreamGraphTabVisibility();
        UpdateStreamIqSquareSize();
        UpdateStartStopExclusive();
        _pollTimer.Start();
    }

    /// <summary>
    /// 定期更新を止め、送受信を停止します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _pollTimer.Stop();
        _tx.Stop();
        _rx.Stop();
    }

    /// <summary>
    /// エラーレート／FFT タブ切替を反映します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnStreamGraphTabChanged(object sender, RoutedEventArgs e)
    {
        UpdateStreamGraphTabVisibility();
    }

    /// <summary>
    /// エラーレートと FFT ホストの表示を切り替えます。
    /// </summary>
    private void UpdateStreamGraphTabVisibility()
    {
        if (StreamErrorChartHost is null || StreamFftChartHost is null || StreamFftTabRadio is null)
        {
            return;
        }

        var showFft = StreamFftTabRadio.IsChecked == true;
        StreamErrorChartHost.Opacity = showFft ? 0 : 1;
        StreamErrorChartHost.IsHitTestVisible = !showFft;
        StreamFftChartHost.Opacity = showFft ? 1 : 0;
        StreamFftChartHost.IsHitTestVisible = showFft;
        StreamErrorChartHost.Visibility = Visibility.Visible;
        StreamFftChartHost.Visibility = Visibility.Visible;
        if (StreamFftLegend is not null)
        {
            StreamFftLegend.Visibility = showFft ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// I-Q 横のグループ凡例を構築します。
    /// </summary>
    private void BuildStreamIqGroupLegend()
    {
        if (StreamIqGroupLegend is null)
        {
            return;
        }

        StreamIqGroupLegend.Children.Clear();
        foreach (var item in IqChartModel.PerformanceGroupLegendItems)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2),
            };
            row.Children.Add(new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(item.R, item.G, item.B)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            });
            row.Children.Add(new TextBlock
            {
                Text = item.Label,
                Foreground = (Brush)FindResource("BrushTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
            });
            StreamIqGroupLegend.Children.Add(row);
        }
    }

    /// <summary>
    /// グラフ行リサイズ時に I-Q を正方形へ合わせます。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnStreamGraphsRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateStreamIqSquareSize();
    }

    /// <summary>
    /// 左グラフ列の高さに合わせて I-Q 枠を揃え、プロットを正方形にします（性能測定 L 相当）。
    /// 「I-Q」タイトルは枠の上に置き、枠本体だけ高さを合わせます。
    /// </summary>
    private void UpdateStreamIqSquareSize()
    {
        if (StreamGraphsRow is null || StreamIqPanelHost is null || StreamIqHost is null
            || StreamIqTitle is null || StreamIqGroupLegend is null || RxIqChart is null
            || StreamGraphsRow.ActualHeight <= 1)
        {
            return;
        }

        var panelHeight = StreamLeftGraphColumn?.ActualHeight > 1
            ? StreamLeftGraphColumn.ActualHeight
            : StreamGraphsRow.ActualHeight;
        if (Math.Abs(StreamIqPanelHost.Height - panelHeight) > 0.5)
        {
            StreamIqPanelHost.Height = panelHeight;
        }

        var titleH = StreamIqTitle.ActualHeight;
        if (titleH <= 0)
        {
            titleH = 16;
        }

        titleH += StreamIqTitle.Margin.Top + StreamIqTitle.Margin.Bottom;
        var padH = StreamIqHost.Padding.Left + StreamIqHost.Padding.Right;
        var padV = StreamIqHost.Padding.Top + StreamIqHost.Padding.Bottom;
        StreamIqGroupLegend.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var legendWidth = Math.Ceiling(StreamIqGroupLegend.DesiredSize.Width)
            + StreamIqGroupLegend.Margin.Left + StreamIqGroupLegend.Margin.Right;

        // 枠高さ = パネル高さ − タイトル行。チャートは枠内の正方形。
        var borderHeight = Math.Floor(panelHeight - titleH);
        var chartSide = Math.Floor(borderHeight - padV);
        chartSide = Math.Clamp(chartSide, 200, 360);
        if (Math.Abs(RxIqChart.Width - chartSide) > 0.5 || Math.Abs(RxIqChart.Height - chartSide) > 0.5)
        {
            RxIqChart.Width = chartSide;
            RxIqChart.Height = chartSide;
        }

        var hostWidth = chartSide + padH;
        if (Math.Abs(StreamIqHost.Width - hostWidth) > 0.5
            || Math.Abs(StreamIqHost.Height - (chartSide + padV)) > 0.5)
        {
            StreamIqHost.Width = hostWidth;
            StreamIqHost.Height = chartSide + padV;
        }

        var panelWidth = hostWidth + legendWidth;
        if (Math.Abs(StreamIqPanelHost.Width - panelWidth) > 0.5)
        {
            StreamIqPanelHost.Width = panelWidth;
        }

        StreamGraphsRow.ColumnDefinitions[1].Width = new GridLength(panelWidth);
    }

    /// <summary>
    /// スタート／ストップの排他と、送信中の設定／入力ロックです。
    /// </summary>
    private void UpdateStartStopExclusive()
    {
        if (TxStartButton is null || TxStopButton is null || RxStartButton is null || RxStopButton is null)
        {
            return;
        }

        var txBusy = _tx.IsBusy;
        var rxBusy = _rx.IsBusy;
        var coverOver = _coverByteCount > StreamCoverImage.MaxBytes;

        // 送信中はストップ以外（速度・入出力・曲情報）を無効化
        if (TxSpeedHost is not null)
        {
            TxSpeedHost.IsEnabled = !txBusy;
        }

        if (TxIoHost is not null)
        {
            TxIoHost.IsEnabled = !txBusy;
        }

        if (TxMetaHost is not null)
        {
            TxMetaHost.IsEnabled = !txBusy;
        }

        TxStartButton.IsEnabled = !txBusy && !rxBusy && !coverOver;
        TxStopButton.IsEnabled = txBusy;
        RxStartButton.IsEnabled = !rxBusy && !txBusy;
        RxStopButton.IsEnabled = rxBusy;
        NotifyRunningStateChanged();
    }

    /// <summary>
    /// 実行中フラグが変わったときだけ RunningStateChanged を通知します。
    /// </summary>
    private void NotifyRunningStateChanged()
    {
        var running = IsRunning;
        if (running == _runningNotified)
        {
            return;
        }

        _runningNotified = running;
        RunningStateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 入出力デバイス一覧の先頭に既定デバイスを置き、続けて実デバイスを並べます。
    /// </summary>
    /// <param name="box">対象コンボボックス。</param>
    /// <param name="isInput">true なら入力デバイス。</param>
    private static void FillDevices(ComboBox box, bool isInput)
    {
        box.Items.Clear();
        box.Items.Add(new StreamDeviceItem(-1, CoreViewText.DefaultDevice));
        if (isInput)
        {
            for (var i = 0; i < WaveIn.DeviceCount; i++)
            {
                var caps = WaveIn.GetCapabilities(i);
                box.Items.Add(new StreamDeviceItem(i, $"{i}: {caps.ProductName}"));
            }
        }
        else
        {
            for (var i = 0; i < WaveOut.DeviceCount; i++)
            {
                var caps = WaveOut.GetCapabilities(i);
                box.Items.Add(new StreamDeviceItem(i, $"{i}: {caps.ProductName}"));
            }
        }

        box.SelectedIndex = 0;
    }

    /// <summary>
    /// 音量スライダーの変更を、横のパーセント表示へ反映します。
    /// </summary>
    /// <param name="sender">音量スライダー。</param>
    /// <param name="e">新しいスライダー値を含む変更引数。</param>
    private void OnStreamVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var text = sender switch
        {
            _ when ReferenceEquals(sender, TxInputVolume) => TxInputVolumeValueText,
            _ when ReferenceEquals(sender, TxOutputVolume) => TxOutputVolumeValueText,
            _ when ReferenceEquals(sender, RxOutputVolume) => RxOutputVolumeValueText,
            _ => RxInputVolumeValueText,
        };
        if (text is not null)
        {
            text.Text = $"{(int)Math.Round(e.NewValue)}%";
        }

        if (ReferenceEquals(sender, RxOutputVolume))
        {
            _rx.SetOutputVolume(e.NewValue / 100.0);
        }
    }

    /// <summary>
    /// 0〜100 のスライダー値を、再生・録音に渡す 0〜1 の音量へ変換します。
    /// </summary>
    /// <param name="slider">音量スライダー。</param>
    /// <returns>0〜1 の音量。</returns>
    private static double ReadVolume(Slider slider)
        => Math.Clamp(slider.Value / 100.0, 0.0, 1.0);

    /// <summary>
    /// コンボで選ばれているデバイス番号を返します。未選択時は既定デバイスです。
    /// </summary>
    /// <param name="box">対象コンボボックス。</param>
    /// <returns>WaveIn / WaveOut のデバイス番号。既定は -1。</returns>
    private static int ReadDeviceNumber(ComboBox box)
        => box.SelectedItem is StreamDeviceItem item ? item.DeviceNumber : -1;

    /// <summary>
    /// 送信入力の音声ファイルを選択します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnBrowseTxWav(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = CoreViewText.FilterAudioFiles,
            FileName = string.IsNullOrWhiteSpace(_txWavPath)
                ? string.Empty
                : System.IO.Path.GetFileName(_txWavPath),
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        ApplyTxWavFile(dlg.FileName);
    }

    /// <summary>
    /// 入力ファイル欄へのドラッグを、WAV / FLAC / MP3 のときだけ受け付けます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ドラッグイベント引数。</param>
    private void OnTxWavDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetAudioFile(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// ドロップされた WAV / FLAC / MP3 を入力ファイルに設定します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ドロップイベント引数。</param>
    private void OnTxWavDrop(object sender, DragEventArgs e)
    {
        if (!TryGetAudioFile(e.Data, out var path))
        {
            return;
        }

        ApplyTxWavFile(path);
        e.Handled = true;
    }

    /// <summary>
    /// 入力ファイルパスを表示し、ファイル入力モードに切り替えます。
    /// </summary>
    /// <param name="path">WAV / FLAC / MP3 のパス。</param>
    private void ApplyTxWavFile(string path)
    {
        _txWavPath = path;
        SetTxWavPathBoxes(_txWavPath);
        TxWavRadio.IsChecked = true;
        UpdateTxInputModeUi();
    }

    /// <summary>
    /// ドロップデータから WAV / FLAC / MP3 の実在ファイルを取り出します。
    /// </summary>
    /// <param name="data">ドラッグデータ。</param>
    /// <param name="path">取り出したファイルパス。</param>
    /// <returns>対応する音声ファイルなら true。</returns>
    private static bool TryGetAudioFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return false;
        }

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                continue;
            }

            var extension = System.IO.Path.GetExtension(file);
            if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            {
                path = file;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 送信入力モード（WAV / 音声）の切替に合わせて下段パネルを切り替えます。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnTxInputModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || TxWavInputPanel is null || TxAudioInputPanel is null)
        {
            return;
        }

        UpdateTxInputModeUi();
    }

    /// <summary>
    /// 送信入力の WAV / 音声パネル表示を同期します。
    /// </summary>
    private void UpdateTxInputModeUi()
    {
        if (TxWavRadio is null || TxWavInputPanel is null || TxAudioInputPanel is null)
        {
            return;
        }

        var useWav = TxWavRadio.IsChecked == true;
        TxWavInputPanel.Visibility = useWav ? Visibility.Visible : Visibility.Collapsed;
        TxAudioInputPanel.Visibility = useWav ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// 入力ファイルパス表示を更新します。
    /// </summary>
    /// <param name="path">表示するファイルパス。</param>
    private void SetTxWavPathBoxes(string path)
    {
        if (TxWavPathBox is not null)
        {
            TxWavPathBox.Text = path;
        }
    }

    /// <summary>
    /// ジャケ写の画像ファイルを選択します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnBrowseCover(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Image|*.png;*.jpg;*.jpeg;*.bmp|All (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        ApplyCoverFile(dlg.FileName);
    }

    /// <summary>
    /// ジャケ写表示領域へのドラッグを、BMP / JPG / PNG のときだけ受け付けます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ドラッグイベント引数。</param>
    private void OnTxCoverDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetCoverFile(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// ドロップされた BMP / JPG / PNG をジャケ写に設定します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ドロップイベント引数。</param>
    private void OnTxCoverDrop(object sender, DragEventArgs e)
    {
        if (!TryGetCoverFile(e.Data, out var path))
        {
            return;
        }

        ApplyCoverFile(path);
        e.Handled = true;
    }

    /// <summary>
    /// ジャケ写ファイルを表示し、プレビューを更新します。
    /// </summary>
    /// <param name="path">BMP / JPG / PNG のパス。</param>
    private void ApplyCoverFile(string path)
    {
        _coverPath = path;
        TxCoverPathBox.Text = _coverPath;
        RefreshCoverPreview();
    }

    /// <summary>
    /// ドロップデータから BMP / JPG / PNG の実在ファイルを取り出します。
    /// </summary>
    /// <param name="data">ドラッグデータ。</param>
    /// <param name="path">取り出したファイルパス。</param>
    /// <returns>対応する画像ファイルなら true。</returns>
    private static bool TryGetCoverFile(IDataObject data, out string path)
    {
        path = string.Empty;
        if (!data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (data.GetData(DataFormats.FileDrop) is not string[] files)
        {
            return false;
        }

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                continue;
            }

            var extension = System.IO.Path.GetExtension(file);
            if (extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                path = file;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ジャケ写の形式切替を反映し、プレビューを更新します。
    /// </summary>
    /// <param name="sender">イベントの発生元。形式のラジオボタン。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnCoverFormatChanged(object sender, RoutedEventArgs e)
    {
        // XAML 読込中に IsChecked 初期化で Checked が飛ぶため、名前付き要素未生成なら無視する。
        if (!IsLoaded || CoverBytesText is null)
        {
            if (sender is RadioButton rb && rb.IsChecked == true)
            {
                _coverFormat = ResolveCoverFormat(rb);
            }

            return;
        }

        if (sender is not RadioButton radio || radio.IsChecked != true)
        {
            return;
        }

        _coverFormat = ResolveCoverFormat(radio);
        RefreshCoverPreview();
    }

    /// <summary>
    /// ラジオボタンの Tag からジャケ写形式を決めます。
    /// </summary>
    /// <param name="rb">形式を選ぶラジオボタン。</param>
    /// <returns>対応するジャケ写形式。不明な Tag は 32x32 カラー。</returns>
    private static StreamCoverFormat ResolveCoverFormat(RadioButton rb) =>
        rb.Tag switch
        {
            "Color48" => StreamCoverFormat.Color48,
            "Gray48" => StreamCoverFormat.Gray48,
            "Gray64" => StreamCoverFormat.Gray64,
            _ => StreamCoverFormat.Color32,
        };

    /// <summary>
    /// 選択中のジャケ写を符号化し、バイト数とプレビューを更新します。
    /// </summary>
    private void RefreshCoverPreview()
    {
        if (CoverBytesText is null || TxCoverPreview is null || CoverSizeOverText is null || TxStartButton is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(_coverPath) || !File.Exists(_coverPath))
        {
            _coverByteCount = 0;
            ApplyCoverByteUi(0, hasError: false);
            TxCoverPreview.Source = null;
            return;
        }

        try
        {
            var bytes = StreamCoverImage.EncodeFile(_coverPath, _coverFormat, out _coverByteCount);
            ApplyCoverByteUi(_coverByteCount, hasError: false);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(_coverPath, UriKind.Absolute);
            bmp.DecodePixelWidth = 72;
            bmp.EndInit();
            bmp.Freeze();
            TxCoverPreview.Source = bmp;
            _ = bytes;
        }
        catch (Exception ex)
        {
            _coverByteCount = 0;
            CoverBytesText.Text = CoreViewText.CoverByteError(ex.Message);
            CoverBytesText.ClearValue(TextBlock.ForegroundProperty);
            CoverSizeOverText.Visibility = Visibility.Collapsed;
            UpdateStartStopExclusive();
        }
    }

    /// <summary>
    /// 圧縮 PNG バイト数表示と、4096 超過時の警告／スタート無効化を反映します。
    /// </summary>
    /// <param name="byteCount">圧縮後のバイト数。</param>
    /// <param name="hasError">符号化エラーがあるか。</param>
    private void ApplyCoverByteUi(int byteCount, bool hasError)
    {
        CoverBytesText.Text = CoreViewText.ByteCountLabel(byteCount);
        var over = !hasError && byteCount > StreamCoverImage.MaxBytes;
        if (over)
        {
            CoverBytesText.Foreground = System.Windows.Media.Brushes.Red;
            CoverSizeOverText.Visibility = Visibility.Visible;
        }
        else
        {
            CoverBytesText.ClearValue(TextBlock.ForegroundProperty);
            // Hidden: レイアウト上の高さを確保したまま非表示にする
            CoverSizeOverText.Visibility = Visibility.Hidden;
        }

        UpdateStartStopExclusive();
    }

    /// <summary>
    /// 画面の設定でストリーム送信を開始します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnTxStart(object sender, RoutedEventArgs e)
    {
        if (_coverByteCount > StreamCoverImage.MaxBytes)
        {
            TxStatusText.Text = CoreViewText.CoverSizeOverMessage;
            return;
        }

        try
        {
            var settings = new StreamTxSettings
            {
                ModeId = ReadModeId(),
                UseWavInput = TxWavRadio.IsChecked == true,
                WavPath = _txWavPath,
                InputDevice = ReadDeviceNumber(TxInputDeviceBox),
                OutputDevice = ReadDeviceNumber(TxOutputDeviceBox),
                InputVolume = ReadVolume(TxInputVolume),
                OutputVolume = ReadVolume(TxOutputVolume),
                Title = TxTitleBox.Text ?? string.Empty,
                Artist = TxArtistBox.Text ?? string.Empty,
                CoverPath = _coverPath,
                CoverFormat = _coverFormat,
            };
            _tx.Start(settings);
            TxStatusText.Text = CoreViewText.SendingNow;
            UpdateStartStopExclusive();
        }
        catch (Exception ex)
        {
            TxStatusText.Text = ex.Message;
        }
    }

    /// <summary>
    /// ストリーム送信の停止を要求します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnTxStop(object sender, RoutedEventArgs e)
    {
        _tx.Stop();
        TxStatusText.Text = CoreViewText.StopRequested;
        UpdateStartStopExclusive();
    }

    /// <summary>
    /// 画面の設定でストリーム受信を開始します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnRxStart(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = new StreamRxSettings
            {
                InputDevice = ReadDeviceNumber(RxInputDeviceBox),
                InputVolume = ReadVolume(RxInputVolume),
                OutputDevice = ReadDeviceNumber(RxOutputDeviceBox),
                OutputVolume = ReadVolume(RxOutputVolume),
            };
            _rx.Start(settings);
            ResetRxGraphs();
            RxStatusText.Text = CoreViewText.ReceivingNow;
            UpdateStartStopExclusive();
        }
        catch (Exception ex)
        {
            RxStatusText.Text = ex.Message;
        }
    }

    /// <summary>
    /// 受信スタート時にエラー率・FFT・I-Q を空の初期状態へ戻します。
    /// </summary>
    private void ResetRxGraphs()
    {
        _errorChart.Clear();
        _fftChart.Clear();
        _fftChart.ShowStereoChannels();
        _iqChart.Clear();
    }

    /// <summary>
    /// ストリーム受信の停止を要求します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnRxStop(object sender, RoutedEventArgs e)
    {
        _rx.Stop();
        RxStatusText.Text = CoreViewText.StopRequested;
        UpdateStartStopExclusive();
    }

    /// <summary>
    /// 選択中のストリーム速度ラジオから変調モード ID を読みます。
    /// </summary>
    /// <returns>選択中のモード。未選択時は 18Kbps。</returns>
    private StreamModeId ReadModeId()
    {
        foreach (var child in FindVisualChildren<RadioButton>(this))
        {
            if (child.GroupName == "StreamRate" && child.IsChecked == true && child.Tag is string tag
                && byte.TryParse(tag, out var id))
            {
                return (StreamModeId)id;
            }
        }

        return StreamModeId.Rate18k;
    }

    /// <summary>
    /// 送受信の完了と、曲情報・グラフを定期的に画面へ反映します。
    /// </summary>
    /// <param name="sender">イベントの発生元。</param>
    /// <param name="e">イベントデータ。</param>
    private void OnPollTick(object? sender, EventArgs e)
    {
        if (_tx.TryConsumeCompletion(out var txOk, out var txMsg))
        {
            TxStatusText.Text = txOk ? txMsg : CoreViewText.FailedWith(txMsg);
            UpdateStartStopExclusive();
        }

        if (_rx.TryConsumeCompletion(out var rxOk, out var rxMsg))
        {
            RxStatusText.Text = rxOk ? rxMsg : CoreViewText.FailedWith(rxMsg);
            UpdateStartStopExclusive();
        }

        UpdateStartStopExclusive();

        if (_rx.IsBusy)
        {
            RxTitleBox.Text = _rx.Title;
            RxArtistBox.Text = _rx.Artist;
            RxRateBox.Text = _rx.DisplayKbps > 0 ? $"{_rx.DisplayKbps} kbps" : "-";
            TryUpdateCoverPreview(_rx.CoverBytes, _rx.CoverReceivedBlocks, _rx.CoverTotalBlocks);
            RxStatusText.Text = CoreViewText.ReceivingPacketStatus(_rx.PacketsReceived, _rx.PacketErrors, _rx.SpeedDeviationPercent);
        }

        // 送信中は送信側ボード、それ以外は受信側（エラー率は受信のみ）
        if (_tx.IsBusy)
        {
            ApplyGraphStatus(_tx.SharedStatus.Read(), includeErrorRate: false);
        }
        else
        {
            ApplyGraphStatus(_rx.SharedStatus.Read(), includeErrorRate: true);
        }
    }

    /// <summary>
    /// 共有ボードの FFT／I-Q（および任意でエラー率）をグラフへ反映します。
    /// </summary>
    /// <param name="snap">共有ボードのスナップショット。</param>
    /// <param name="includeErrorRate">エラー率も反映するか。</param>
    private void ApplyGraphStatus(CoreExecutionStatus snap, bool includeErrorRate)
    {
        if (includeErrorRate && !snap.IsAnalyzing && snap.ErrorRateSamples.Count > 0)
        {
            foreach (var sample in snap.ErrorRateSamples)
            {
                _errorChart.AddSample(sample.LatestPercent, sample.DecoderKind);
            }

            _errorChart.Tick();
        }

        if (snap.FftGraph.FftSize > 0)
        {
            _fftChart.ReplacePoints(snap.FftGraph.LeftPoints, snap.FftGraph.RightPoints, snap.FftGraph.IsStereo);
        }

        if (snap.IqGraph.Points.Count > 0)
        {
            _iqChart.ReplacePoints(snap.IqGraph.Points, snap.IqGraph.ModulationScheme);
        }
    }

    /// <summary>
    /// 受信ジャケ写の取得ブロック数か検証済みバイト列が変わったときだけ取得率とプレビューを更新します。
    /// </summary>
    /// <param name="cover">検証済みのジャケ写バイト列（未完了なら空）。</param>
    /// <param name="receivedBlocks">取得済みブロック数。</param>
    /// <param name="totalBlocks">総ブロック数（未受信なら 0）。</param>
    private void TryUpdateCoverPreview(byte[] cover, int receivedBlocks, int totalBlocks)
    {
        if (RxCoverSizeText is null || RxCoverBytesText is null || RxCoverRateText is null || RxCoverImage is null)
        {
            return;
        }

        if (receivedBlocks == _lastRxCoverBlocks && totalBlocks == _lastRxCoverTotal && ReferenceEquals(cover, _lastRxCover))
        {
            return;
        }

        _lastRxCoverBlocks = receivedBlocks;
        _lastRxCoverTotal = totalBlocks;
        _lastRxCover = cover;
        RxCoverRateText.Text = CoreViewText.CoverRateLabel(receivedBlocks, totalBlocks);
        // 未完了の間は総ブロック数から見込みのバイト数を出す
        RxCoverBytesText.Text = CoreViewText.ByteCountLabel(
            cover.Length > 0 ? cover.Length : totalBlocks * StreamConstants.MetaBlockDataBytes);

        if (cover.Length == 0)
        {
            RxCoverSizeText.Text = CoreViewText.SizeLabelUnknown;
            RxCoverImage.Source = null;
            return;
        }

        try
        {
            using var ms = new MemoryStream(cover);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            RxCoverImage.Source = bmp;
            RxCoverSizeText.Text = CoreViewText.SizeLabelPixels(bmp.PixelWidth, bmp.PixelHeight);
        }
        catch
        {
            RxCoverSizeText.Text = CoreViewText.SizeLabelUnknown;
            RxCoverImage.Source = null;
        }
    }

    /// <summary>
    /// ビジュアルツリー配下から指定型の要素を列挙します。
    /// </summary>
    /// <param name="root">探索の起点。</param>
    /// <returns>一致した子孫要素。</returns>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is null)
        {
            yield break;
        }

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// ストリーム画面の入出力デバイス項目です。
    /// </summary>
    private sealed class StreamDeviceItem
    {
        /// <summary>
        /// デバイス項目を作ります。
        /// </summary>
        /// <param name="deviceNumber">WaveIn / WaveOut の番号。既定は -1。</param>
        /// <param name="name">表示名。</param>
        public StreamDeviceItem(int deviceNumber, string name)
        {
            DeviceNumber = deviceNumber;
            Name = name;
        }

        /// <summary>デバイス番号。</summary>
        public int DeviceNumber { get; }

        /// <summary>コンボボックスの表示名。</summary>
        public string Name { get; }
    }
}
