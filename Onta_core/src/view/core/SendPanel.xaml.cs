using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using NAudioWaveOut = NAudio.Wave.WaveOut;
using Onta.Core;
using Onta.View.Language;

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
            ActiveSubcarriers: ReadSelectedInt("Subcarrier", 16),
            ModulationScheme: ReadSelectedModulation(),
            BlockInterleaveFactor: ReadRepeatCount(),
            InputFilePath: InputPathBox.Text,
            WriteWav: writeWav,
            WavOutputPath: WavPathBox.Text,
            PlayAudio: !writeWav,
            AudioDeviceNumber: ReadSelectedAudioDeviceNumber(),
            AudioDeviceName: ReadSelectedAudioDeviceName(),
            AudioVolume: ReadAudioVolume(),
            WavSampleRate: ReadWavSampleRate());
    }

    /// <summary>
    /// 保存済み設定を UI へ反映します。
    /// </summary>
    /// <param name="snapshot">反映する送信設定スナップショット。</param>
    public void ApplySnapshot(SendSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        MonoRadio.IsChecked = snapshot.ChannelMode == ChannelMode.Mono;
        StereoRadio.IsChecked = snapshot.ChannelMode == ChannelMode.Stereo;
        SetCheckedRadio("Subcarrier", snapshot.ActiveSubcarriers.ToString(), fallbackTag: "16");
        SetCheckedRadio("Modulation", snapshot.ModulationScheme switch
        {
            ModulationScheme.Bpsk => "Bpsk",
            ModulationScheme.Qpsk => "Qpsk",
            ModulationScheme.Psk8 => "Psk8",
            ModulationScheme.Qam16 => "Qam16",
            ModulationScheme.Qam64 => "Qam64",
            _ => "Bpsk"
        }, fallbackTag: "Bpsk");
        SetCheckedRadio("Interleave", snapshot.BlockInterleaveFactor.ToString(), fallbackTag: "1");

        InputPathBox.Text = snapshot.InputFilePath ?? string.Empty;
        WriteWavRadio.IsChecked = snapshot.WriteWav;
        PlayAudioRadio.IsChecked = !snapshot.WriteWav;
        WavPathBox.Text = snapshot.WavOutputPath ?? string.Empty;
        SelectAudioDevice(snapshot.AudioDeviceNumber);
        SelectWavSampleRate(snapshot.WavSampleRate);
        var volume = Math.Clamp(snapshot.AudioVolume * 100.0, 0.0, 100.0);
        AudioVolumeSlider.Value = volume;
        AudioVolumeValueText.Text = $"{(int)Math.Round(volume)}%";

        UpdateOutputModePanels();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 音量スライダー値を 0〜1 に正規化して返します。未初期化時は 0.8。
    /// </summary>
    /// <returns>0〜1 の音量。</returns>
    private double ReadAudioVolume()
    {
        if (AudioVolumeSlider is null)
        {
            return 0.8;
        }

        return Math.Clamp(AudioVolumeSlider.Value / 100.0, 0.0, 1.0);
    }

    /// <summary>
    /// UIで選択されたインタリーブ係数を返します。
    /// </summary>
    /// <returns>1〜2に丸めたインタリーブ係数。</returns>
    private int ReadRepeatCount()
    {
        return Math.Clamp(ReadSelectedInt("Interleave", 1), 1, 2);
    }

    /// <summary>
    /// チャネル／出力モード等のラジオ変更時にパネル表示を更新し、SettingsChanged を発火します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnSettingsChanged(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { IsChecked: false })
        {
            return;
        }

        UpdateOutputModePanels();

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// スタート押下で現在設定のスナップショットを OutputRequested / StartRequested へ通知します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        OutputRequested?.Invoke(this, CreateSnapshot());
        StartRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// ストップ押下で StopRequested を発火します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        StopRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 送信操作（設定・スタート／ストップ）の有効/無効を切り替えます。
    /// 受信実行中など、送信を触らせないときに使います。
    /// </summary>
    /// <param name="enabled">有効なら true。</param>
    public void SetInteractionEnabled(bool enabled)
    {
        if (_transmissionRunning)
        {
            return;
        }

        SettingsHost.IsEnabled = enabled;
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = false;
        if (enabled)
        {
            UpdateOutputModePanels();
        }
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

    /// <summary>
    /// 音声出力デバイス選択変更時に SettingsChanged を発火します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">選択変更イベント引数。</param>
    private void OnAudioDeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 音量スライダー変更時に表示パーセントを更新し、SettingsChanged を発火します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">新しいスライダー値を含む変更引数。</param>
    private void OnAudioVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AudioVolumeValueText is not null)
        {
            AudioVolumeValueText.Text = $"{(int)Math.Round(e.NewValue)}%";
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 入力ファイル選択ダイアログを開き、未設定なら WAV 出力パスも既定名で埋めます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnBrowseInput(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = CoreViewText.DialogSelectInputFile,
            Filter = $"{CoreViewText.FilterAllFiles}|{CoreViewText.FilterPng}",
            InitialDirectory = AppPaths.InputDir
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        ApplyInputFile(dlg.FileName);
    }

    /// <summary>
    /// 送信ファイルの指定を外し、パス欄を空にします。WAV 出力先は残します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnClearInput(object sender, RoutedEventArgs e)
    {
        InputPathBox.Text = string.Empty;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 送信ファイル欄へのドラッグを、ファイルのときだけ受け付けます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ドラッグイベント引数。</param>
    private void OnInputPathDragOver(object sender, DragEventArgs e)
    {
        e.Effects = TryGetDroppedFile(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// ドロップされたファイルを送信ファイルとして設定します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ドロップイベント引数。</param>
    private void OnInputPathDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedFile(e.Data, out var path))
        {
            return;
        }

        ApplyInputFile(path);
        e.Handled = true;
    }

    /// <summary>
    /// 送信ファイルパスを表示し、WAV 出力先が空なら既定名を埋めます。
    /// </summary>
    /// <param name="path">送信するファイルの絶対パス。</param>
    private void ApplyInputFile(string path)
    {
        InputPathBox.Text = path;
        if (string.IsNullOrWhiteSpace(WavPathBox.Text))
        {
            WavPathBox.Text = Path.Combine(AppPaths.OutputDir, Path.ChangeExtension(Path.GetFileName(path), ".wav"));
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// ドロップデータから先頭の実在ファイルを取り出します。
    /// </summary>
    /// <param name="data">ドラッグデータ。</param>
    /// <param name="path">取り出したファイルパス。</param>
    /// <returns>ファイルなら true。</returns>
    private static bool TryGetDroppedFile(IDataObject data, out string path)
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
            if (!string.IsNullOrWhiteSpace(file) && File.Exists(file))
            {
                path = file;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// NAudio 出力デバイスを列挙し、コンボボックスへ既定デバイス込みで登録します。
    /// </summary>
    private void InitializeAudioDevices()
    {
        AudioDeviceComboBox.Items.Clear();
        AudioDeviceComboBox.Items.Add(new AudioDeviceItem(DefaultAudioDeviceNumber, CoreViewText.DefaultDevice));

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

    /// <summary>
    /// WAV 出力／音声出力ラジオに応じて対応パネルの表示を切り替えます。
    /// </summary>
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
        if (WavSampleRateCombo is not null)
        {
            WavSampleRateCombo.Visibility = writeWav ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// WAV 出力のサンプリング周波数コンボの選択値を返します。
    /// </summary>
    /// <returns><see cref="OfdmConfig.ModemSampleRate"/>、48000、96000 のいずれか。未選択時は <see cref="OfdmConfig.ModemSampleRate"/>。</returns>
    private int ReadWavSampleRate()
    {
        if (WavSampleRateCombo?.SelectedItem is ComboBoxItem item
            && SampleRateTag.TryRead(item.Tag, out var rate))
        {
            return SendSettingsSnapshot.NormalizeWavSampleRate(rate);
        }

        return OfdmConfig.ModemSampleRate;
    }

    /// <summary>
    /// WAV 出力のサンプリング周波数コンボを指定値に合わせます。
    /// </summary>
    /// <param name="sampleRate">サンプリング周波数（Hz）。</param>
    private void SelectWavSampleRate(int sampleRate)
    {
        if (WavSampleRateCombo is null)
        {
            return;
        }

        var rate = SendSettingsSnapshot.NormalizeWavSampleRate(sampleRate);
        foreach (var item in WavSampleRateCombo.Items)
        {
            if (item is ComboBoxItem combo
                && SampleRateTag.TryRead(combo.Tag, out var tagRate)
                && tagRate == rate)
            {
                WavSampleRateCombo.SelectedItem = combo;
                return;
            }
        }
    }

    /// <summary>
    /// WAV サンプリング周波数の変更を設定変更として通知します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">選択変更引数。</param>
    private void OnWavSampleRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || WavSampleRateCombo is null)
        {
            return;
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 選択中の音声出力デバイス番号を返します。未選択時は既定デバイス番号。
    /// </summary>
    /// <returns>NAudio デバイス番号（既定は -1）。</returns>
    private int ReadSelectedAudioDeviceNumber()
    {
        return AudioDeviceComboBox.SelectedItem is AudioDeviceItem item
            ? item.DeviceNumber
            : DefaultAudioDeviceNumber;
    }

    /// <summary>
    /// 選択中の音声出力デバイス表示名を返します。
    /// </summary>
    /// <returns>デバイス名。未選択時は「既定デバイス」。</returns>
    private string ReadSelectedAudioDeviceName()
    {
        return AudioDeviceComboBox.SelectedItem is AudioDeviceItem item
            ? item.Name
            : CoreViewText.DefaultDevice;
    }

    /// <summary>
    /// WAV 出力先の保存ダイアログを開き、選択パスをテキストボックスへ反映します。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">ルーティングイベント引数。</param>
    private void OnBrowseWav(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = CoreViewText.DialogWavOutputDestination,
            Filter = CoreViewText.FilterWav,
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

    /// <summary>
    /// 指定ラジオグループでチェック中の Tag を整数として読み取ります。
    /// </summary>
    /// <param name="groupName">ラジオの GroupName。</param>
    /// <param name="fallback">該当なし／解析失敗時の既定値。</param>
    /// <returns>選択値。なければ fallback。</returns>
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

    /// <summary>
    /// 変調ラジオの選択 Tag を ModulationScheme へ変換します。
    /// </summary>
    /// <returns>選択中の変調方式。未選択時は Bpsk。</returns>
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
                "Psk8" => ModulationScheme.Psk8,
                "Qam16" => ModulationScheme.Qam16,
                "Qam64" => ModulationScheme.Qam64,
                _ => ModulationScheme.Bpsk
            };
        }

        return ModulationScheme.Bpsk;
    }

    /// <summary>
    /// 指定グループ内で Tag が一致するラジオをチェックします。見つからなければ fallbackTag を選択します。
    /// </summary>
    /// <param name="groupName">ラジオの GroupName。</param>
    /// <param name="tag">選択したい Tag。null ならフォールバックのみ。</param>
    /// <param name="fallbackTag">一致なし時に選ぶ Tag。</param>
    private void SetCheckedRadio(string groupName, string? tag, string fallbackTag)
    {
        RadioButton? fallback = null;
        foreach (var radio in FindRadios(this))
        {
            if (!string.Equals(radio.GroupName, groupName, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(radio.Tag as string, fallbackTag, StringComparison.Ordinal))
            {
                fallback = radio;
            }

            if (tag is not null && string.Equals(radio.Tag as string, tag, StringComparison.Ordinal))
            {
                radio.IsChecked = true;
                return;
            }
        }

        fallback?.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
    }

    /// <summary>
    /// コンボボックスで指定デバイス番号を選択します。見つからなければ先頭（既定）を選びます。
    /// </summary>
    /// <param name="deviceNumber">選択する NAudio デバイス番号。</param>
    private void SelectAudioDevice(int deviceNumber)
    {
        for (var i = 0; i < AudioDeviceComboBox.Items.Count; i++)
        {
            if (AudioDeviceComboBox.Items[i] is AudioDeviceItem item && item.DeviceNumber == deviceNumber)
            {
                AudioDeviceComboBox.SelectedIndex = i;
                return;
            }
        }

        AudioDeviceComboBox.SelectedIndex = 0;
    }

    /// <summary>
    /// 論理ツリーを再帰走査し、配下の RadioButton を列挙します。
    /// </summary>
    /// <param name="root">走査起点。</param>
    /// <returns>見つかったラジオボタン列。</returns>
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

    /// <summary>
    /// 音声出力デバイスのコンボ項目（番号と表示名）です。
    /// </summary>
    /// <param name="DeviceNumber">NAudio デバイス番号。</param>
    /// <param name="Name">表示名。</param>
    private sealed record AudioDeviceItem(int DeviceNumber, string Name);
}




