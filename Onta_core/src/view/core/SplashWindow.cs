using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Onta.View.Core;

/// <summary>
/// 起動時に短時間表示するスプラッシュウィンドウです。
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// スプラッシュ画像を表示するウィンドウを生成します。
    /// </summary>
    /// <param name="imagePath">表示する PNG の絶対パス。</param>
    /// <param name="displayMilliseconds">表示時間（ミリ秒）。</param>
    public SplashWindow(string imagePath, int displayMilliseconds = 1600)
    {
        Width = 512;
        Height = 256;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = false;
        // スプラッシュ PNG のチャコール基調と揃える
        Background = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1B, 0x1C, 0x1E));

        var image = new Image
        {
            Stretch = System.Windows.Media.Stretch.Uniform,
            SnapsToDevicePixels = true
        };

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        image.Source = bitmap;
        Content = image;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(200, displayMilliseconds))
        };
        _timer.Tick += OnTimerTick;
    }

    /// <summary>
    /// 表示を開始し、一定時間後に自動で閉じます。
    /// </summary>
    public void ShowTimed()
    {
        Show();
        _timer.Start();
    }

    /// <summary>
    /// タイマー経過でウィンドウを閉じます。
    /// </summary>
    /// <param name="sender">イベント送信元。</param>
    /// <param name="e">イベント引数。</param>
    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        Close();
    }

    /// <summary>
    /// 同梱マニュアルフォルダからスプラッシュ画像パスを解決します。
    /// </summary>
    /// <returns>存在する画像パス。無ければ null。</returns>
    public static string? ResolveSplashImagePath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "manual", "onta_splash_512x256.png"),
            Path.Combine(AppContext.BaseDirectory, "onta_splash_512x256.png"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Onta_manual", "onta_splash_512x256.png")),
            Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "Onta_manual", "onta_splash_512x256.png")),
            @"C:\proj\Onta\Onta_manual\onta_splash_512x256.png",
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
