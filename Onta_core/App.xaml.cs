namespace Onta.View.Core;

/// <summary>
/// WPF アプリケーションのエントリです。
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>
    /// 起動時に表示言語を決め、ClickOnce の登録（アプリと機能のアイコン・インストール時のデスクトップショートカットの選択）を反映し、コアホストを開始し、スプラッシュのあとメインウィンドウを表示します。
    /// </summary>
    /// <param name="e">起動引数（--lang ja / --lang en で表示言語を指定。無ければ OS の言語で、日本語以外は英語）。</param>
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        Onta.View.Language.AppLanguage.ApplyFromArgs(e.Args);
        ClickOnceRegistration.ApplyOnStartup();
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


