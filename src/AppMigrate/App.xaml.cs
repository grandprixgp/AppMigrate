using System.Windows;
using AppMigrate.Services.Lan;

namespace AppMigrate;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Elevated helper launched by the Nearby screen: add the firewall rule and exit, no UI.
        if (e.Args.Contains(FirewallService.ConfigureArgument, StringComparer.OrdinalIgnoreCase))
        {
            Shutdown(FirewallService.ConfigureElevated());
            return;
        }

        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
