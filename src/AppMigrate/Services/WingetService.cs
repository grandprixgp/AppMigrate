using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AppMigrate.Models;

namespace AppMigrate.Services;

public sealed record WingetRow(string Name, string Id, string Version, string Source, bool IdTruncated, bool NameTruncated, bool IsMsix, bool IsArp, string IdPrefix = "");
public sealed record ExportedPackage(string Id, string Version, string Source);
public sealed record WingetInventory(IReadOnlyList<WingetRow> Rows, string? Warning, IReadOnlyList<ExportedPackage> Exported)
{
    public WingetInventory(IReadOnlyList<WingetRow> rows, string? warning)
        : this(rows, warning, Array.Empty<ExportedPackage>()) { }
}

public sealed class WingetService
{
    // "Found <name> [<id>]" from `winget show`; the leading word is localized, the brackets are not.
    private static readonly Regex FoundLine = new(@"(?m)^\S[^\r\n]*\[([^\]\s]+)\]\s*$", RegexOptions.Compiled);
    private static readonly Regex TrailingVersionPart = new(@"^(.*\S\s+v?\d+(?:\.\d+)*)\.\d+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingVersion = new(@"^(.*?)\s+v?\d[\w.\-]*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Parenthetical = new(@"\s*\([^)]*\)", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, string?> _idCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string?> _storeIdCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<WingetInventory> GetInventoryAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "winget.exe",
            new[] { "list", "--accept-source-agreements", "--disable-interactivity" },
            null,
            TimeSpan.FromMinutes(2),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<WingetRow> rows = ParseList(result.Output);
        // Newer winget versions print full IDs when redirected. Only pay for an export when the
        // table actually truncated something.
        IReadOnlyList<ExportedPackage> exported = rows.Any(r => r.IdTruncated)
            ? await GetExportedPackagesAsync(cancellationToken).ConfigureAwait(false)
            : Array.Empty<ExportedPackage>();
        rows = RepairTruncatedIds(rows, exported);
        if (rows.Count == 0)
        {
            // A working winget always lists at least App Installer, so an empty table means the
            // command failed or its output could not be read. Say so instead of degrading silently.
            string warning = result.Output.Trim();
            if (warning.Length > 240) warning = warning[..237] + "...";
            if (result.ExitCode == 0 || string.IsNullOrWhiteSpace(warning))
            {
                warning = "WinGet inventory could not be read (exit " + result.ExitCode + ").";
            }
            return new WingetInventory(Array.Empty<WingetRow>(), warning, exported);
        }
        return new WingetInventory(rows, null, exported);
    }

    public async Task<IReadOnlyList<ExportedPackage>> GetExportedPackagesAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetTempPath(), $"appmigrate-export-{Guid.NewGuid():N}.json");
        try
        {
            await ProcessRunner.RunAsync(
                "winget.exe",
                new[] { "export", "-o", path, "--include-versions", "--accept-source-agreements", "--disable-interactivity" },
                null,
                TimeSpan.FromMinutes(2),
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(path)) return Array.Empty<ExportedPackage>();
            return ParseExport(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Export only improves ID accuracy; the list output is still usable without it.
            return Array.Empty<ExportedPackage>();
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    public async Task<string?> ResolveIdAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string cacheKey = name;
        if (_idCache.TryGetValue(cacheKey, out string? cached)) return cached;

        foreach (string candidate in Candidates(name))
        {
            ProcessResult show = await ProcessRunner.RunAsync(
                "winget.exe",
                new[] { "show", "--name", candidate, "--exact", "--accept-source-agreements", "--disable-interactivity" },
                null,
                TimeSpan.FromSeconds(30),
                cancellationToken).ConfigureAwait(false);

            string? id = ParseShowId(show.Output);
            if (id != null)
            {
                _idCache[cacheKey] = id;
                return id;
            }

            string? searchId = await SearchIdAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (searchId != null)
            {
                _idCache[cacheKey] = searchId;
                return searchId;
            }
        }

        _idCache[cacheKey] = null;
        return null;
    }

    public async Task<string?> ResolveStoreIdAsync(string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_storeIdCache.TryGetValue(name, out string? cached)) return cached;

        ProcessResult result = await ProcessRunner.RunAsync(
            "winget.exe",
            new[] { "search", "--name", name, "--exact", "--source", "msstore", "--accept-source-agreements", "--disable-interactivity" },
            null,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        string? id = PickSearchId(result.Output, name, storeOnly: true);
        _storeIdCache[name] = id;
        return id;
    }

    public async Task<ProcessResult> InstallAsync(
        AppItem app,
        bool force,
        bool interactive,
        bool matchScope,
        bool pinVersion,
        CancellationToken cancellationToken,
        Action<string>? onLine = null)
    {
        var args = new List<string>
        {
            "install", "--id", app.Id, "--exact", "--source",
            string.Equals(app.InstallType, "msstore", StringComparison.OrdinalIgnoreCase) ? "msstore" : "winget",
            "--accept-package-agreements", "--accept-source-agreements"
        };

        if (string.Equals(app.InstallType, "winget", StringComparison.OrdinalIgnoreCase))
        {
            if (!interactive)
            {
                args.Add("--silent");
                args.Add("--disable-interactivity");
            }
            if (matchScope && (app.Scope == "user" || app.Scope == "machine")) args.Add("--scope");
            if (matchScope && (app.Scope == "user" || app.Scope == "machine")) args.Add(app.Scope);
            // winget lists ranges such as "> 270.4" or "Unknown"; only pin real version numbers.
            if (pinVersion && app.Version.Length > 0 && char.IsDigit(app.Version[0]))
            {
                args.Add("--version");
                args.Add(app.Version);
            }
        }
        else if (!interactive)
        {
            args.Add("--disable-interactivity");
        }

        if (force) args.Add("--force");
        args.AddRange(CommandLine.Split(app.WingetArgs));

        return await ProcessRunner.RunAsync("winget.exe", args, onLine, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> SearchIdAsync(string candidate, CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "winget.exe",
            new[] { "search", "--name", candidate, "--source", "winget", "--accept-source-agreements", "--disable-interactivity" },
            null,
            TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        return PickSearchId(result.Output, candidate, storeOnly: false);
    }

    internal static string? ParseShowId(string output)
    {
        // Several matches print a table instead of a single "Found" line.
        if (WingetTable.Parse(output) != null) return null;
        Match match = FoundLine.Match(output);
        if (!match.Success) return null;
        string id = match.Groups[1].Value.Trim();
        if (id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) || id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase)) return null;
        return PackageIds.IsWingetId(id) || PackageIds.IsStoreId(id) ? id : null;
    }

    /// <summary>
    /// Picks the search row for <paramref name="candidate"/>. An exact name (versions kept) wins;
    /// a version-insensitive match is accepted only when it is the single such row, so that
    /// "Python 3.12" never resolves to a different Python release.
    /// </summary>
    internal static string? PickSearchId(string output, string candidate, bool storeOnly)
    {
        WingetTable? table = WingetTable.Parse(output);
        if (table == null || table.ColumnCount < 2) return null;

        string strict = TextUtil.NormalizeStrict(candidate);
        string loose = TextUtil.NormalizeLoose(candidate);
        var looseMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string[] cells in table.Rows)
        {
            string name = cells[0];
            string id = cells[1];
            if (id.Length == 0 || IsTruncated(id)) continue;
            if (storeOnly)
            {
                id = id.ToUpperInvariant();
                if (!PackageIds.IsStoreId(id)) continue;
            }

            if (strict.Length > 0 && TextUtil.NormalizeStrict(name) == strict) return id;
            if (loose.Length > 0 && TextUtil.NormalizeLoose(name) == loose) looseMatches.Add(id);
        }
        return looseMatches.Count == 1 ? looseMatches.First() : null;
    }

    internal static IReadOnlyList<WingetRow> ParseList(string raw)
    {
        WingetTable? table = WingetTable.Parse(raw);
        if (table == null || table.ColumnCount < 3) return Array.Empty<WingetRow>();

        // Columns are Name, Id, Version, then optionally Available, then Source.
        int sourceColumn = table.ColumnCount >= 4 ? table.ColumnCount - 1 : -1;

        var rows = new List<WingetRow>();
        foreach (string[] cells in table.Rows)
        {
            string name = cells[0];
            string rawId = cells[1];
            if (name.Length == 0) continue;

            bool isMsix = rawId.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);
            bool isArp = rawId.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase);
            bool idTruncated = IsTruncated(rawId);
            bool nameTruncated = name.EndsWith('…');
            string id = TextUtil.CleanAscii(rawId);
            string version = TextUtil.CleanAscii(cells[2]);
            string source = sourceColumn >= 0 ? cells[sourceColumn] : "";
            if (source.Length > 0 && char.IsDigit(source[0])) source = "";

            if (!idTruncated && id.Length > 0 && !isMsix && !isArp && id.Any(char.IsWhiteSpace)) continue;

            string prefix = idTruncated ? TextUtil.CleanAscii(rawId.Replace("…", "")) : "";
            rows.Add(new WingetRow(name, idTruncated ? "" : id, version, source, idTruncated, nameTruncated, isMsix, isArp, prefix));
        }
        return rows;
    }

    internal static IReadOnlyList<ExportedPackage> ParseExport(string json)
    {
        var packages = new List<ExportedPackage>();
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("Sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
        {
            return packages;
        }

        foreach (JsonElement source in sources.EnumerateArray())
        {
            string sourceName = source.TryGetProperty("SourceDetails", out JsonElement details) &&
                                details.TryGetProperty("Name", out JsonElement nameElement)
                ? nameElement.GetString() ?? ""
                : "";
            if (!source.TryGetProperty("Packages", out JsonElement list) || list.ValueKind != JsonValueKind.Array) continue;

            foreach (JsonElement package in list.EnumerateArray())
            {
                string id = package.TryGetProperty("PackageIdentifier", out JsonElement idElement) ? idElement.GetString() ?? "" : "";
                string version = package.TryGetProperty("Version", out JsonElement versionElement) ? versionElement.GetString() ?? "" : "";
                if (id.Length > 0) packages.Add(new ExportedPackage(id, version, sourceName));
            }
        }
        return packages;
    }

    /// <summary>Completes truncated list IDs from the untruncated `winget export` set when the prefix is unambiguous.</summary>
    internal static IReadOnlyList<WingetRow> RepairTruncatedIds(IReadOnlyList<WingetRow> rows, IReadOnlyList<ExportedPackage> exported)
    {
        if (exported.Count == 0 || !rows.Any(r => r.IdTruncated)) return rows;

        var claimed = new HashSet<string>(rows.Where(r => r.Id.Length > 0).Select(r => r.Id), StringComparer.OrdinalIgnoreCase);
        var available = exported.Where(p => !claimed.Contains(p.Id)).ToList();
        var repaired = new List<WingetRow>(rows.Count);
        foreach (WingetRow row in rows)
        {
            if (!row.IdTruncated || row.IsMsix || row.IsArp || row.IdPrefix.Length < 3)
            {
                repaired.Add(row);
                continue;
            }

            List<ExportedPackage> matches = available
                .Where(p => p.Id.StartsWith(row.IdPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                repaired.Add(row);
                continue;
            }

            ExportedPackage match = matches[0];
            available.Remove(match);
            repaired.Add(row with
            {
                Id = match.Id,
                IdTruncated = false,
                IdPrefix = "",
                Source = row.Source.Length > 0 ? row.Source : match.Source
            });
        }
        return repaired;
    }

    /// <summary>Name variants to try, most specific first: "App 3.12.1 (x64)" → "App 3.12.1" → "App 3.12" → "App".</summary>
    internal static IEnumerable<string> Candidates(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();
        void Add(string value)
        {
            value = value.Trim();
            if (value.Length > 0 && seen.Add(value)) ordered.Add(value);
        }

        Add(name);
        string current = Parenthetical.Replace(name, "").Trim();
        Add(current);

        Match part = TrailingVersionPart.Match(current);
        if (part.Success) Add(part.Groups[1].Value);
        Match whole = TrailingVersion.Match(current);
        if (whole.Success) Add(whole.Groups[1].Value);

        // Each candidate costs two winget processes, so keep the list short.
        return ordered.Take(4);
    }

    private static bool IsTruncated(string id)
        => id.Contains('…') || TextUtil.LooksTruncated(id);
}
