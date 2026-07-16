using System.Configuration;
using System.Data;
using System.Windows;

namespace LittleFish.Installer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(argument => string.Equals(
                argument,
                "--package-smoke-test",
                StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                LittleFish.Installer.MainWindow.RunPackageSelfTests();
                Shutdown(0);
            }
            catch
            {
                Shutdown(1);
            }
            return;
        }

        base.OnStartup(e);
    }
}

