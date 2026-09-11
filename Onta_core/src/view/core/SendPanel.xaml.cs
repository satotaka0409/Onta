using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudioWaveOut = NAudio.Wave.WaveOut;
using Onta.Core;

namespace Onta.View.Core;

/// <summary>
/// 送信設定の編集と送信開始/停止操作を担うパネルです。
/// </summary>
public partial class SendPanel : UserControl
{
    private const int DefaultAudioDeviceNumber = -1;

    public event EventHandler? SettingsChanged;
    public event EventHandler<SendSettingsSnapshot>? OutputRequested;
    public event EventHandler? StartRequested;
    public event EventHandler? StopRequested;

    private bool _transmissionRunning;

    /// <summary>
    /// 送信パネルを初期化します。
    /// </summary>
    public SendPanel()
    {
        InitializeComponent();
        StereoRadio.Checked += OnSettingsChanged;
        MonoRadio.Checked += OnSettingsChanged;
        InitializeAudioDevices();
        UpdateOutputModePanels();
    }

    /// <summary>
    /// 現在のUI設定から送信スナップショットを生成します。
    /// </summary>
    /// <returns>送信設定スナップショット。</returns>
    public SendSettingsSnapshot CreateSnapshot()
    {
        var writeWav = WriteWavRadio.IsChecked == true;
        return new SendSettingsSnapshot(
            ChannelMode: MonoRadio.IsChecked == true ? Onta.Core.ChannelMode.Mono : Onta.Core.ChannelMode.Stereo,
            ActiveSubcarriers: ReadSelectedInt("Subcarrier", 9),
            ModulationScheme: ReadSelectedModulation(),
            BlockInterleaveFactor: ReadRepeatCount(),
            InputFilePath: InputPathBox.Text,
            WriteWav: writeWav,
            WavOutputPath: WavPathBox.Text,
            PlayAudio: !writeWav,
            AudioDeviceNumber: ReadSelectedAudioDeviceNumber(),
            AudioDeviceName: ReadSelectedAudioDeviceName());
    }

    /// <summary>
    /// UIで選択されたインタリーブ係数を返します。
    /// </summary>
    /// <returns>1〜2に丸めたインタリーブ係数。</returns>
    private int ReadRepeatCount()
    {
        return Math.Clamp(ReadSelectedInt("Interleave", 1), 1, 2);
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: false })
        {
            return;
        }

        UpdateOutputModePanels();

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        OutputRequested?.Invoke(this, CreateSnapshot());
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 送信実行中状態に合わせて入力UIの有効/無効を切り替えます。
    /// </summary>
    /// <param name="isRunning">送信中なら true。</param>
    public void SetTransmissionRunning(bool isRunning)
    {
        _transmissionRunning = isRunning;
        SettingsHost.IsEnabled = !isRunning;
        StartButton.IsEnabled = !isRunning;
        StopButton.IsEnabled = isRunning;
        if (!isRunning)
        {
            UpdateOutputModePanels();
        }
    }

    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select input file",
            Filter = "すべてのファイル (*.*)|*.*|PNG (*.png)|*.png",
            InitialDirectory = AppPaths.InputDir
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        InputPathBox.Text = dlg.FileName;
        if (string.IsNullOrWhiteSpace(WavPathBox.Text))
        {
            WavPathBox.Text = Path.Combine(AppPaths.OutputDir, Path.ChangeExtension(Path.GetFileName(dlg.FileName), ".wav"));
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InitializeAudioDevices()
    {
        AudioDeviceComboBox.Items.Clear();
        AudioDeviceComboBox.Items.Add(new AudioDeviceItem(DefaultAudioDeviceNumber, "既定デバイス"));

        try
        {
            for (var i = 0; i < NAudioWaveOut.DeviceCount; i++)
            {
                var caps = NAudioWaveOut.GetCapabilities(i);
                AudioDeviceComboBox.Items.Add(new AudioDeviceItem(i, caps.ProductName));
            }
        }
        catch
        {
            // デバイス列挙失敗時は既定デバイスのみで継続する。
        }

        AudioDeviceComboBox.SelectedIndex = 0;
    }

    private void UpdateOutputModePanels()
    {
        // 初期化順の都合で未生成コントロールなら何もしない。
        if (WriteWavRadio is null || WavOutputPanel is null || AudioOutputPanel is null)
        {
            return;
        }

        if (_transmissionRunning)
        {
            return;
        }

        var writeWav = WriteWavRadio.IsChecked == true;
        WavOutputPanel.Visibility = writeWav ? Visibility.Visible : Visibility.Collapsed;
        AudioOutputPanel.Visibility = writeWav ? Visibility.Collapsed : Visibility.Visible;
    }

    private int ReadSelectedAudioDeviceNumber()
    {
        return AudioDeviceComboBox.SelectedItem is AudioDeviceItem item
            ? item.DeviceNumber
            : DefaultAudioDeviceNumber;
    }

    private string ReadSelectedAudioDeviceName()
    {
        return AudioDeviceComboBox.SelectedItem is AudioDeviceItem item
            ? item.Name
            : "既定デバイス";
    }

    private void OnBrowseWav(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "WAV 出力先",
            Filter = "WAV (*.wav)|*.wav",
            InitialDirectory = AppPaths.OutputDir,
            FileName = string.IsNullOrWhiteSpace(WavPathBox.Text) ? "onta_out.wav" : Path.GetFileName(WavPathBox.Text)
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        WavPathBox.Text = dlg.FileName;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private int ReadSelectedInt(string groupName, int fallback)
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName == groupName && radio.IsChecked == true && radio.Tag is string s && int.TryParse(s, out var v))
            {
                return v;
            }
        }

        return fallback;
    }

    private ModulationScheme ReadSelectedModulation()
    {
        foreach (var radio in FindRadios(this))
        {
            if (radio.GroupName != "Modulation" || radio.IsChecked != true || radio.Tag is not string tag)
            {
                continue;
            }

            return tag switch
            {
                "Bpsk" => ModulationScheme.Bpsk,
                "Qpsk" => ModulationScheme.Qpsk,
                "Qam16" => ModulationScheme.Qam16,
                "Qam64" => ModulationScheme.Qam64,
                _ => ModulationScheme.Bpsk
            };
        }

        return ModulationScheme.Bpsk;
    }

    private static IEnumerable<RadioButton> FindRadios(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is RadioButton radio)
            {
                yield return radio;
            }

            if (child is DependencyObject dep)
            {
                foreach (var nested in FindRadios(dep))
                {
                    yield return nested;
                }
            }
        }
    }

    private sealed record AudioDeviceItem(int DeviceNumber, string Name);
}




