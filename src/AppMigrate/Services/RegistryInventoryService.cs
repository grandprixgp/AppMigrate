using AppMigrate.Models;
using Microsoft.Win32;
using System.Text.RegularExpressions;

namespace AppMigrate.Services;

public sealed class RegistryInventoryService
{
    public Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<AppItem>>(() =>
        {
            var results = new List<AppItem>();
            var tasks = new[]
            {
                Task.Run(() => ScanHive(RegistryHive.LocalMachine, RegistryView.Registry64, "machine", cancellationToken), cancellationToken),
                Task.Run(() => ScanHive(RegistryHive.LocalMachine, RegistryView.Registry32, "machine", cancellationToken), cancellationToken),
                Task.Run(() => ScanHive(RegistryHive.CurrentUser, RegistryView.Default, "user", cancellationToken), cancellationToken)
            };

            Task.WaitAll(tasks, cancellationToken);
            foreach (var task in tasks) results.AddRange(task.Result);
            return results;
        }, cancellationToken);
    }

    private static IEnumerable<AppItem> ScanHive(RegistryHive hive, RegistryView view, string scope, CancellationToken cancellationToken)
    {
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
        using RegistryKey? uninstall = baseKey.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall == null) yield break;

        foreach (string subKeyName in uninstall.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RegistryKey? key = uninstall.OpenSubKey(subKeyName);
            if (key == null) continue;

            string? displayName = key.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(displayName)) continue;
            if (Convert.ToInt32(key.GetValue("SystemComponent", 0)) == 1) continue;
            if (Convert.ToInt32(key.GetValue("NoDisplay", 0)) == 1) continue;
            if ((key.GetValue("ParentKeyName") as string)?.Length > 0) continue;

            string? releaseType = key.GetValue("ReleaseType") as string;
            if (releaseType is "Update" or "Hotfix" or "Security Update") continue;
            if (displayName.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ||
                displayName.StartsWith("Update for ", StringComparison.OrdinalIgnoreCase) ||
                displayName.StartsWith("Security Update for ", StringComparison.OrdinalIgnoreCase) ||
                displayName.StartsWith("Hotfix for ", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(displayName, @"\b(Update|Updater|Helper|Telemetry)\b", RegexOptions.IgnoreCase)) continue;

            string uninstallString = key.GetValue("UninstallString") as string ?? "";
            string quietUninstallString = key.GetValue("QuietUninstallString") as string ?? "";
            if (uninstallString.Length == 0 && quietUninstallString.Length == 0) continue;

            yield return new AppItem
            {
                Name = displayName.Trim(),
                Source = "registry",
                InstallType = "manual",
                Version = key.GetValue("DisplayVersion") as string ?? "",
                Publisher = key.GetValue("Publisher") as string ?? "",
                Scope = scope,
                UninstallString = uninstallString,
                InstallLocation = key.GetValue("InstallLocation") as string ?? "",
                Notes = ""
            };
        }
    }
}
