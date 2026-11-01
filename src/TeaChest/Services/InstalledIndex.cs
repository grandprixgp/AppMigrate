using TeaChest.Models;

namespace TeaChest.Services;

public sealed class InstalledIndex
{
    // Names keep their version numbers (NormalizeStrict) so "Python 3.11" does not satisfy "Python 3.12".
    public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PackageFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when WinGet's own inventory loaded, so package IDs can be trusted on their own.</summary>
    public bool HasWingetInventory { get; set; }

    public static async Task<InstalledIndex> BuildAsync(CancellationToken cancellationToken)
    {
        var registry = new RegistryInventoryService();
        var appx = new AppxInventoryService();
        var winget = new WingetService();

        Task<IReadOnlyList<AppItem>> registryTask = registry.ScanAsync(cancellationToken);
        Task<IReadOnlyList<AppItem>> appxTask = appx.ScanAsync(cancellationToken);
        Task<WingetInventory> wingetTask = winget.GetInventoryAsync(cancellationToken);

        await Task.WhenAll(registryTask, appxTask, wingetTask).ConfigureAwait(false);
        return Build(registryTask.Result, appxTask.Result, wingetTask.Result);
    }

    internal static InstalledIndex Build(IEnumerable<AppItem> registry, IEnumerable<AppItem> appx, WingetInventory winget)
    {
        var index = new InstalledIndex
        {
            HasWingetInventory = winget.Warning == null && winget.Rows.Count > 0
        };

        foreach (AppItem item in registry)
        {
            index.AddName(item.Name);
        }

        foreach (AppItem item in appx)
        {
            index.AddName(item.Name);
            if (item.PackageFamilyName.Length > 0) index.PackageFamilies.Add(item.PackageFamilyName);
            if (item.PackageFullName.Length > 0) index.Ids.Add(item.PackageFullName);
        }

        foreach (WingetRow row in winget.Rows)
        {
            index.AddName(row.Name);
            if (!string.IsNullOrWhiteSpace(row.Id)) index.Ids.Add(row.Id);
        }

        foreach (ExportedPackage package in winget.Exported)
        {
            index.Ids.Add(package.Id);
        }

        return index;
    }

    public bool Contains(AppItem app)
    {
        if (!string.IsNullOrWhiteSpace(app.Id) && Ids.Contains(app.Id)) return true;
        if (!string.IsNullOrWhiteSpace(app.PackageFamilyName) && PackageFamilies.Contains(app.PackageFamilyName)) return true;
        if (!string.IsNullOrWhiteSpace(app.PackageFullName) && Ids.Contains(app.PackageFullName)) return true;

        // WinGet correlates installed programs to package IDs, so when its inventory is available a
        // missing ID means "not installed". Guessing by name here causes false skips.
        bool hasPackageId = !string.IsNullOrWhiteSpace(app.Id) &&
                            (app.IsWinget || string.Equals(app.InstallType, "msstore", StringComparison.OrdinalIgnoreCase));
        if (hasPackageId && HasWingetInventory) return false;

        string name = TextUtil.NormalizeStrict(app.Name);
        return name.Length > 0 && Names.Contains(name);
    }

    private void AddName(string name)
    {
        string key = TextUtil.NormalizeStrict(name);
        if (key.Length > 0) Names.Add(key);
    }
}
