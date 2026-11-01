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

        // TODO: a proper headless mode right here - `AppMigrate.exe scan --out apps.json` and
        // `AppMigrate.exe install apps.json --yes` - so the cli/ PowerShell scripts can retire and
        // the same exe works in unattended setups (autounattend, Intune, a USB stick).
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
