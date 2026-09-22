namespace Onta.View.Core;

/// <summary>
/// WPF アプリケーションのエントリです。
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        CoreBackgroundHost.Start();
        CoreBackgroundHost.QueueWarmup();
        base.OnStartup(e);

        // StartupUri と InitializeComponent の二重ロードで x:Name が衝突するため、ここで明示生成する。
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        CoreBackgroundHost.Stop();
        base.OnExit(e);
    }
}


