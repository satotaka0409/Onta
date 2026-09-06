using System.IO;
using System.Windows;
using Onta.Core;

namespace Onta.View;

/// <summary>
/// メインウィンドウです（送信上・受信下・タブは見積以下。画面仕様.mdc）。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SendPanel.SettingsChanged += (_, _) => RefreshEstimate();
        SendPanel.StartRequested += OnStartRequested;
        RefreshEstimate();
    }

    private void RefreshEstimate()
    {
        EstimatePanel.UpdateEstimate(SendPanel.CreateSnapshot());
    }

    private async void OnStartRequested(object? sender, EventArgs e)
    {
        var snap = SendPanel.CreateSnapshot();
        if (string.IsNullOrWhiteSpace(snap.InputFilePath) || !File.Exists(snap.InputFilePath))
        {
            MessageBox.Show(this, "ファイルを選択してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (snap.WriteWav && string.IsNullOrWhiteSpace(snap.WavOutputPath))
        {
            MessageBox.Show(this, "WAV 出力先を指定してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var profile = CodecProfileFactory.FromSnapshot(snap);
        var wavPath = snap.WriteWav
            ? snap.WavOutputPath
            : Path.Combine(AppPaths.OutputDir, Path.ChangeExtension(Path.GetFileName(snap.InputFilePath), ".wav"));
        var restoredPath = Path.Combine(
            AppPaths.OutputDir,
            Path.GetFileNameWithoutExtension(snap.InputFilePath) + "_restored" + Path.GetExtension(snap.InputFilePath));

        SendPanel.IsEnabled = false;
        try
        {
            var original = await File.ReadAllBytesAsync(snap.InputFilePath).ConfigureAwait(true);
            var codec = new FileWavCodec(profile);
            var decoded = await Task.Run(() => codec.EncodeDecodeRoundTrip(snap.InputFilePath, wavPath, restoredPath))
                .ConfigureAwait(true);

            var match = original.AsSpan().SequenceEqual(decoded);
            MessageBox.Show(
                this,
                $"符号化完了\n\n" +
                $"繰り返し回数: {FormatRepeatCount(snap.BlockInterleaveFactor)}\n" +
                $"チャンネル: {snap.ChannelMode}\n" +
                $"サブキャリア: {snap.ActiveSubcarriers}\n" +
                $"変調: {snap.ModulationScheme}\n" +
                $"WAV: {wavPath}\n" +
                $"復元: {restoredPath}\n" +
                $"一致: {(match ? "OK" : "NG")}",
                "Onta",
                MessageBoxButton.OK,
                match ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Onta エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SendPanel.IsEnabled = true;
        }
    }

    private static string FormatRepeatCount(int factor) => factor switch
    {
        1 => "なし",
        2 => "2回",
        3 => "3回",
        _ => $"{factor}回"
    };
}
