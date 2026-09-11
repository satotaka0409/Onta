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
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        CoreBackgroundHost.Stop();
        base.OnExit(e);
    }
}


