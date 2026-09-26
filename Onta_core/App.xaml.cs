namespace Onta.View.Core;

/// <summary>
/// WPF アプリケーションのエントリです。
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// 起動時にコアホストを開始し、スプラッシュのあとメインウィンドウを表示します。
    /// </summary>
    /// <param name="e">起動引数。</param>
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        CoreBackgroundHost.Start();
        CoreBackgroundHost.QueueWarmup();
        base.OnStartup(e);

        var splashPath = SplashWindow.ResolveSplashImagePath();
        if (splashPath is not null)
        {
            var splash = new SplashWindow(splashPath);
            splash.ShowTimed();
        }

        // StartupUri と InitializeComponent の二重ロードで x:Name が衝突するため、ここで明示生成する。
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// 終了時にコアバックグラウンドホストを停止します。
    /// </summary>
    /// <param name="e">終了引数。</param>
    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        CoreBackgroundHost.Stop();
        base.OnExit(e);
    }
}


