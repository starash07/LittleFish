using System.Windows;
using System.Windows.Threading;

namespace LittleFish.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _instance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var request = LaunchRequest.FromArguments(e.Args);
        var hosted = e.Args.Any(arg => string.Equals(arg, "--seas-hosted", StringComparison.OrdinalIgnoreCase));
        if (!hosted)
        {
            _instance = new SingleInstanceCoordinator();
            if (!_instance.TryBecomePrimary())
            {
                if (await _instance.ForwardAsync(request))
                {
                    Shutdown();
                    return;
                }

                if (!_instance.TryBecomePrimary())
                {
                    System.Windows.MessageBox.Show("LittleFish 正忙，请稍后重试。", "LittleFish", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
            }
        }

        var window = new MainWindow();
        MainWindow = window;
        _instance?.StartListening(command => Dispatcher.BeginInvoke(
            () => window.HandleLaunchRequest(command), DispatcherPriority.Normal));
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        window.Show();
        if (request.FilePath is not null)
        {
            _ = Dispatcher.BeginInvoke(() => window.HandleLaunchRequest(request), DispatcherPriority.Loaded);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}

