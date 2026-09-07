using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NAudioWaveOut = NAudio.Wave.WaveOut;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// 送信設定パネルです（チャンネル／SC／変調／繰り返し回数／入出力）。
/// </summary>
public partial class SendPanel : UserControl
{
    private const int DefaultAudioDeviceNumber = -1;

    public event EventHandler? SettingsChanged;
    public event EventHandler<SendSettingsSnapshot>? OutputRequested;
    public event EventHandler? StartRequested;

    public SendPanel()
    {
        InitializeComponent();
        StereoRadio.Checked += OnSettingsChanged;
        MonoRadio.Checked += OnSettingsChanged;
        InitializeAudioDevices();
        UpdateAudioDeviceEnabledState();
    }

    /// <summary>
    /// 現在の送信設定スナップショットを返します。
    /// </summary>
    public SendSettingsSnapshot CreateSnapshot()
    {
        return new SendSettingsSnapshot(
            ChannelMode: MonoRadio.IsChecked == true ? Onta.Core.ChannelMode.Mono : Onta.Core.ChannelMode.Stereo,
            ActiveSubcarriers: ReadSelectedInt("Subcarrier", 9),
            ModulationScheme: ReadSelectedModulation(),
            BlockInterleaveFactor: ReadRepeatCount(),
            InputFilePath: InputPathBox.Text,
            WriteWav: WriteWavCheck.IsChecked == true,
            WavOutputPath: WavPathBox.Text,
            PlayAudio: PlayAudioCheck.IsChecked == true);
    }

    /// <summary>
    /// 繰り返し回数（なし=1 / 2回=2 / 3回=3）。data_struct.mdc のブロックインターリーブ倍率。
    /// </summary>
    private int ReadRepeatCount()
    {
        if (Repeat3Radio.IsChecked == true)
        {
            return 3;
        }

        if (Repeat2Radio.IsChecked == true)
        {
            return 2;
        }

        return 1;
    }

    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: false })
        {
            return;
        }

        UpdateAudioDeviceEnabledState();

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        OutputRequested?.Invoke(this, CreateSnapshot());
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "送信ファイルを選択",
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
            // デバイス列挙失敗時は既定デバイスのみで継続。
        }

        AudioDeviceComboBox.SelectedIndex = 0;
    }

    private void UpdateAudioDeviceEnabledState()
    {
        var enabled = PlayAudioCheck.IsChecked == true;
        AudioDeviceComboBox.IsEnabled = enabled;
        AudioDeviceComboBox.Opacity = enabled ? 1.0 : 0.6;
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
