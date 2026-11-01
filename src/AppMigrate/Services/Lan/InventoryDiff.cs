using AppMigrate.Models;

namespace AppMigrate.Services.Lan;

public sealed class InventoryDiffResult
{
    public List<AppItem> MissingHere { get; } = new();
    public List<AppItem> AlreadyHere { get; } = new();
    public List<AppItem> OnlyHere { get; } = new();
}

public static class InventoryDiff
{
    /// <summary>
    /// Splits a remote inventory into apps this PC lacks and apps it already has, and lists local
    /// apps the remote does not have. Missing apps keep the remote's selection; apps already here
    /// are deselected so they never end up in an install plan.
    /// </summary>
    public static InventoryDiffResult Compare(IEnumerable<AppItem> remote, InstalledIndex installedHere, IEnumerable<AppItem>? localApps)
    {
        var result = new InventoryDiffResult();
        List<AppItem> remoteApps = remote.ToList();
        foreach (AppItem app in remoteApps)
        {
            if (installedHere.Contains(app))
            {
                app.IsSelected = false;
                result.AlreadyHere.Add(app);
            }
            else
            {
                result.MissingHere.Add(app);
            }
        }

        if (localApps != null)
        {
            var remoteIds = new HashSet<string>(remoteApps.Where(a => a.Id.Length > 0).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            var remoteNames = new HashSet<string>(remoteApps.Select(a => TextUtil.NormalizeStrict(a.Name)), StringComparer.OrdinalIgnoreCase);
            foreach (AppItem local in localApps)
            {
                bool onRemote = (local.Id.Length > 0 && remoteIds.Contains(local.Id)) || remoteNames.Contains(TextUtil.NormalizeStrict(local.Name));
                if (!onRemote) result.OnlyHere.Add(local);
            }
        }
        return result;
    }
}
