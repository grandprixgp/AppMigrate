using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TeaChest.Models;

public sealed class AppItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string InstallType { get; set; } = "manual";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Scope { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Notes { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string StoreSearchName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string InstallerType { get; set; } = "";
    public string InstallerArgs { get; set; } = "";
    public string WingetArgs { get; set; } = "";
    public bool DefaultSelected { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsManual => string.Equals(InstallType, "manual", StringComparison.OrdinalIgnoreCase);
    public bool IsStore => string.Equals(InstallType, "msstore", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Source, "msix", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Source, "msstore", StringComparison.OrdinalIgnoreCase)
                           || Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);
    public bool IsWinget => string.Equals(InstallType, "winget", StringComparison.OrdinalIgnoreCase);
    public bool IsDownload => string.Equals(InstallType, "download", StringComparison.OrdinalIgnoreCase);
    public string TypeLabel => InstallType switch
    {
        "winget" => "WinGet",
        "msstore" => "Store",
        "download" => "Download",
        _ => "Manual"
    };

    public string Initial
    {
        get
        {
            string name = Name.Trim();
            return name.Length == 0 ? "?" : name[..1].ToUpperInvariant();
        }
    }

    public string SearchBlob => string.Join(' ', Name, Id, Category, InstallType, Publisher, Notes);

    public string IdentityKey => !string.IsNullOrWhiteSpace(Id) ? Id.ToLowerInvariant() : TextUtil.NormalizeLoose(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppItem Clone()
    {
        var clone = (AppItem)MemberwiseClone();
        clone.PropertyChanged = null;
        return clone;
    }

    public void MergeFrom(AppItem other)
    {
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(other.Name)) Name = other.Name;
        if (string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(other.Id)) Id = other.Id;
        if (string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(other.Version)) Version = other.Version;
        if (string.IsNullOrWhiteSpace(Publisher) && !string.IsNullOrWhiteSpace(other.Publisher)) Publisher = other.Publisher;
        if (string.IsNullOrWhiteSpace(Scope) && !string.IsNullOrWhiteSpace(other.Scope)) Scope = other.Scope;
        if (string.IsNullOrWhiteSpace(PackageFamilyName) && !string.IsNullOrWhiteSpace(other.PackageFamilyName)) PackageFamilyName = other.PackageFamilyName;
        if (string.IsNullOrWhiteSpace(PackageFullName) && !string.IsNullOrWhiteSpace(other.PackageFullName)) PackageFullName = other.PackageFullName;
        if (string.IsNullOrWhiteSpace(Category) && !string.IsNullOrWhiteSpace(other.Category)) Category = other.Category;
        if (string.IsNullOrWhiteSpace(Notes) && !string.IsNullOrWhiteSpace(other.Notes)) Notes = other.Notes;
        if (string.IsNullOrWhiteSpace(UninstallString) && !string.IsNullOrWhiteSpace(other.UninstallString)) UninstallString = other.UninstallString;
        if (string.IsNullOrWhiteSpace(InstallLocation) && !string.IsNullOrWhiteSpace(other.InstallLocation)) InstallLocation = other.InstallLocation;
        if (string.IsNullOrWhiteSpace(StoreSearchName) && !string.IsNullOrWhiteSpace(other.StoreSearchName)) StoreSearchName = other.StoreSearchName;
        if (string.IsNullOrWhiteSpace(DownloadUrl) && !string.IsNullOrWhiteSpace(other.DownloadUrl)) DownloadUrl = other.DownloadUrl;
        if (string.IsNullOrWhiteSpace(Sha256) && !string.IsNullOrWhiteSpace(other.Sha256)) Sha256 = other.Sha256;
        if (string.IsNullOrWhiteSpace(InstallerType) && !string.IsNullOrWhiteSpace(other.InstallerType)) InstallerType = other.InstallerType;
        if (string.IsNullOrWhiteSpace(InstallerArgs) && !string.IsNullOrWhiteSpace(other.InstallerArgs)) InstallerArgs = other.InstallerArgs;
        if (string.IsNullOrWhiteSpace(WingetArgs) && !string.IsNullOrWhiteSpace(other.WingetArgs)) WingetArgs = other.WingetArgs;
        if (TypePriority(other) > TypePriority(this))
        {
            if (other.IsWinget && string.IsNullOrWhiteSpace(other.Id)) return;
            Source = other.Source;
            InstallType = other.InstallType;
            if (!string.IsNullOrWhiteSpace(other.Id)) Id = other.Id;
            if (!string.IsNullOrWhiteSpace(other.Name) && other.Name.Length < Name.Length) Name = other.Name;
        }
    }

    public static int TypePriority(AppItem item)
    {
        if (item.IsWinget) return 4;
        if (string.Equals(item.InstallType, "msstore", StringComparison.OrdinalIgnoreCase)) return 3;
        if (item.IsStore) return 2;
        return 1;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
