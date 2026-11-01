using AppMigrate.Models;

namespace AppMigrate.Services;

public sealed class InstalledIndex
{
    public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PackageFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<InstalledIndex> BuildAsync(CancellationToken cancellationToken)
    {
        var index = new InstalledIndex();
        var registry = new RegistryInventoryService();
        var appx = new AppxInventoryService();
        var winget = new WingetService();

        Task<IReadOnlyList<AppItem>> registryTask = registry.ScanAsync(cancellationToken);
        Task<IReadOnlyList<AppItem>> appxTask = appx.ScanAsync(cancellationToken);
        Task<WingetInventory> wingetTask = winget.GetInventoryAsync(cancellationToken);

        await Task.WhenAll(registryTask, appxTask, wingetTask).ConfigureAwait(false);

        foreach (AppItem item in registryTask.Result)
        {
            string key = TextUtil.NormalizeLoose(item.Name);
            if (key.Length > 0) index.Names.Add(key);
        }

        foreach (AppItem item in appxTask.Result)
        {
            string key = TextUtil.NormalizeLoose(item.Name);
            if (key.Length > 0) index.Names.Add(key);
            if (item.PackageFamilyName.Length > 0) index.PackageFamilies.Add(item.PackageFamilyName);
            if (item.PackageFullName.Length > 0) index.Ids.Add(item.PackageFullName);
        }

        foreach (WingetRow row in wingetTask.Result.Rows)
        {
            string key = TextUtil.NormalizeLoose(row.Name);
            if (key.Length > 0) index.Names.Add(key);
            if (!string.IsNullOrWhiteSpace(row.Id)) index.Ids.Add(row.Id);
        }

        return index;
    }

    public bool Contains(AppItem app)
    {
        if (!string.IsNullOrWhiteSpace(app.Id) && Ids.Contains(app.Id)) return true;
        if (!string.IsNullOrWhiteSpace(app.PackageFamilyName) && PackageFamilies.Contains(app.PackageFamilyName)) return true;
        if (!string.IsNullOrWhiteSpace(app.PackageFullName) && Ids.Contains(app.PackageFullName)) return true;
        string name = TextUtil.NormalizeLoose(app.Name);
        return name.Length > 0 && Names.Contains(name);
    }
}
