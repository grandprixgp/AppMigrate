using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TeaChest.Models;

namespace TeaChest.Services;

public sealed class ManifestService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<ManifestDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using FileStream stream = File.OpenRead(path);
        ManifestDocument? document = await JsonSerializer.DeserializeAsync<ManifestDocument>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (document == null) throw new InvalidDataException("The file is not a valid app migration manifest.");
        document.LoadWarnings = ManifestSanitizer.Sanitize(document, ManifestTrust.LocalFile);
        return document;
    }

    public static ManifestDocument CreateDocument(IEnumerable<AppItem> apps)
    {
        List<AppItem> list = apps.ToList();
        return new ManifestDocument
        {
            SchemaVersion = 1,
            GeneratedAt = DateTimeOffset.Now.ToString("O"),
            GeneratedBy = "TeaChest",
            SourceComputer = Environment.MachineName,
            WindowsVersion = Environment.OSVersion.VersionString,
            Stats = new ManifestStats
            {
                Detected = list.Count,
                Selected = list.Count(a => a.IsSelected)
            },
            Apps = list.Select(AppRecord.FromItem).ToList()
        };
    }

    // TODO: also write a configuration.winget (WinGet Configuration / DSC YAML) next to apps.json.
    // Then `winget configure apps.winget` or Dev Home could rebuild the PC without TeaChest at
    // all, which feels like the right kind of "no lock-in".
    public async Task SaveAsync(string path, IEnumerable<AppItem> apps, CancellationToken cancellationToken = default)
    {
        ManifestDocument document = CreateDocument(apps);

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string json = JsonSerializer.Serialize(document, JsonOptions);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".csv"), BuildCsv(document.Apps), new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.ChangeExtension(path, ".md"), BuildMarkdown(document.Apps), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    public static List<AppItem> ToItems(ManifestDocument document)
        => document.Apps.Select(record => record.ToItem()).ToList();

    private static string BuildCsv(IReadOnlyList<AppRecord> apps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Include,Name,Id,Source,InstallType,Version,Category,Publisher,Scope,PackageFamilyName,StoreSearchName,Notes,UninstallString,InstallLocation,DownloadUrl,Sha256,InstallerType,InstallerArgs,WingetArgs");
        foreach (AppRecord app in apps)
        {
            string[] values =
            {
                app.Include ? "TRUE" : "FALSE",
                app.Name, app.Id, app.Source, app.InstallType, app.Version, app.Category, app.Publisher,
                app.Scope, app.PackageFamilyName, app.StoreSearchName, app.Notes, app.UninstallString,
                app.InstallLocation, app.DownloadUrl, app.Sha256, app.InstallerType, app.InstallerArgs, app.WingetArgs
            };
            sb.AppendLine(string.Join(",", values.Select(CsvField)));
        }
        return sb.ToString();
    }

    private static string BuildMarkdown(IReadOnlyList<AppRecord> apps)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Apps to reinstall");
        sb.AppendLine();
        sb.AppendLine($"Selected **{apps.Count(a => a.Include)}** of {apps.Count} apps.");
        sb.AppendLine();
        sb.AppendLine("| App | Type | Version | ID |");
        sb.AppendLine("|---|---|---|---|");
        foreach (AppRecord app in apps.Where(a => a.Include).OrderBy(a => a.Category).ThenBy(a => a.Name))
        {
            sb.AppendLine($"| {Md(app.Name)} | {Md(app.InstallType)} | {Md(app.Version)} | `{Md(app.Id)}` |");
        }
        return sb.ToString();
    }

    private static string CsvField(string? value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static string Md(string? value)
        => (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
