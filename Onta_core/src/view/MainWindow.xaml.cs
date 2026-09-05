using System.Windows;

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

    private void OnStartRequested(object? sender, EventArgs e)
    {
        var snap = SendPanel.CreateSnapshot();
        if (string.IsNullOrWhiteSpace(snap.InputFilePath))
        {
            MessageBox.Show(this, "ファイルを選択してください。", "Onta", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(
            this,
            $"スタート（たたき台）\n\n" +
            $"チャンネル: {snap.ChannelMode}\n" +
            $"サブキャリア: {snap.ActiveSubcarriers}\n" +
            $"変調: {snap.ModulationScheme}\n" +
            $"インターリーブ: ×{snap.BlockInterleaveFactor}\n" +
            $"入力: {snap.InputFilePath}\n" +
            $"WAV出力: {(snap.WriteWav ? snap.WavOutputPath : "なし")}\n" +
            $"音声出力: {(snap.PlayAudio ? "あり" : "なし")}",
            "Onta",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
