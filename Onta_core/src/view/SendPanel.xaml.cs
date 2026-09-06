using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// 送信設定パネルです（チャンネル／SC／変調／繰り返し回数／入出力）。
/// </summary>
public partial class SendPanel : UserControl
{
    public event EventHandler? SettingsChanged;
    public event EventHandler? StartRequested;

    public SendPanel()
    {
        InitializeComponent();
        StereoRadio.Checked += OnSettingsChanged;
        MonoRadio.Checked += OnSettingsChanged;
        RepeatNoneRadio.Checked += OnSettingsChanged;
        Repeat2Radio.Checked += OnSettingsChanged;
        Repeat3Radio.Checked += OnSettingsChanged;
    }

    /// <summary>
    /// 現在の送信設定スナップショットを返します。
    /// </summary>
    public SendSettingsSnapshot CreateSnapshot()
    {
        return new SendSettingsSnapshot(
            ChannelMode: MonoRadio.IsChecked == true ? ChannelMode.Mono : ChannelMode.Stereo,
            ActiveSubcarriers: ReadSelectedInt("Subcarrier", 18),
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

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        StartRequested?.Invoke(this, EventArgs.Empty);
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
                _ => ModulationScheme.Qpsk
            };
        }

        return ModulationScheme.Qpsk;
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
}
