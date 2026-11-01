using AppMigrate.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace AppMigrate.Services;

public sealed class AppxInventoryService
{
    public Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<AppItem>>(() =>
        {
            var items = new List<AppItem>();
            var manager = new PackageManager();
            IEnumerable<Package> packages = manager.FindPackagesForUser(string.Empty);
            foreach (Package package in packages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    if (package.SignatureKind == PackageSignatureKind.System) continue;

                    string familyName = package.Id.FamilyName;
                    string name = package.DisplayName;
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                    {
                        name = package.Id.Name;
                    }
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var version = package.Id.Version;

                    items.Add(new AppItem
                    {
                        Name = name.Trim(),
                        Id = "MSIX\\" + familyName,
                        Source = "msix",
                        InstallType = "manual",
                        Version = $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}",
                        Publisher = package.PublisherDisplayName ?? "",
                        PackageFamilyName = familyName,
                        PackageFullName = package.Id.FullName ?? "",
                        InstallLocation = package.InstalledPath ?? "",
                        StoreSearchName = name.Trim(),
                        Notes = ""
                    });
                }
                catch
                {
                    // Some system packages deny property access; ignore those.
                }
            }
            return items;
        }, cancellationToken);
    }
}
