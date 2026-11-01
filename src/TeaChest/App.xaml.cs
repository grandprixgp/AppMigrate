using System.Windows;
using TeaChest.Services;
using TeaChest.Services.Lan;

namespace TeaChest;

// TODO: the big one - a Linux version, on its own branch. New PC, same apps, on Linux too.
// WPF is the first wall, so the UI goes to Avalonia or similar, and everything that isn't UI moves
// into a cross-platform core first (do that on main so the branch doesn't rot).
//   - Inventory: apt/dpkg, dnf/rpm, pacman, Flatpak, Snap, AppImage, .desktop files, and a filter
//     for the thousands of base-system packages nobody thinks of as "apps".
//   - Install: one backend per package manager behind the same plan and retry flow, pkexec for UAC.
//   - Nearby: ufw/firewalld instead of netsh, and check broadcast behaves the same.
//   - Windows bits to replace: the registry, AppX, DWM title bar colours, %LOCALAPPDATA% paths.
//   - Packaging and CI: Flatpak first, then AppImage, deb, rpm.
//   - Manifests: a per-platform ID map, and a WinGet-to-distro mapping if Windows to Linux is in
//     scope. Plenty of apps won't have an equivalent, so the plan has to say so clearly.

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

        // TODO: a proper headless mode right here - `TeaChest.exe scan --out teachest.json` and
        // `TeaChest.exe install teachest.json --yes` - so the cli/ PowerShell scripts can retire and
        // the same exe works in unattended setups (autounattend, Intune, a USB stick).
        AppPaths.MoveLegacyData();
        base.OnStartup(e);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
