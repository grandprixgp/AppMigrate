using TeaChest.Models;

namespace TeaChest.Services.Lan;

public sealed class InventoryDiffResult
{
    public List<AppItem> MissingHere { get; } = new();
    public List<AppItem> AlreadyHere { get; } = new();
    public List<AppItem> OnlyHere { get; } = new();
}

// TODO: "time machine" - quietly save a dated manifest to OneDrive every week or so, then let
// people diff against any of them later. Most migrations happen *after* a PC dies, and by then
// it's too late to scan it. This diff would work as-is on two saved manifests.
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

    /// <summary>
    /// Compares two saved manifests: apps the before one had and the after one lacks, apps both
    /// have, and apps only the after one has. Both documents must already have been through
    /// <see cref="ManifestSanitizer"/>; this trusts their IDs and does not sanitise again.
    /// </summary>
    public static InventoryDiffResult CompareManifests(ManifestDocument before, ManifestDocument after)
    {
        var result = new InventoryDiffResult();
        InstalledIndex afterIndex = FromManifest(after);
        InstalledIndex beforeIndex = FromManifest(before);

        foreach (AppRecord record in before.Apps)
        {
            AppItem app = record.ToItem();
            if (afterIndex.Contains(app)) result.AlreadyHere.Add(app);
            else result.MissingHere.Add(app);
        }

        foreach (AppRecord record in after.Apps)
        {
            AppItem app = record.ToItem();
            if (!beforeIndex.Contains(app)) result.OnlyHere.Add(app);
        }

        return result;
    }

    /// <summary>
    /// Indexes a manifest as if it were this PC's inventory. A manifest's IDs come from the PC it
    /// was scanned on, so they can be trusted on their own.
    /// </summary>
    internal static InstalledIndex FromManifest(ManifestDocument document)
    {
        var index = new InstalledIndex { HasWingetInventory = true };
        foreach (AppRecord app in document.Apps)
        {
            string name = TextUtil.NormalizeStrict(app.Name);
            if (name.Length > 0) index.Names.Add(name);
            if (!string.IsNullOrWhiteSpace(app.Id)) index.Ids.Add(app.Id);
            if (!string.IsNullOrWhiteSpace(app.PackageFamilyName)) index.PackageFamilies.Add(app.PackageFamilyName);
            if (!string.IsNullOrWhiteSpace(app.PackageFullName)) index.Ids.Add(app.PackageFullName);
        }

        return index;
    }
}
