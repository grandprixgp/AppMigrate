using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using AppMigrate.Models;

namespace AppMigrate.Services;

public sealed record WingetRow(string Name, string Id, string Version, string Source, bool IdTruncated, bool NameTruncated, bool IsMsix, bool IsArp);
public sealed record WingetInventory(IReadOnlyList<WingetRow> Rows, string? Warning);

public sealed class WingetService
{
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
        if (result.ExitCode != 0 && rows.Count == 0)
        {
            string warning = result.Output.Trim();
            if (warning.Length > 240) warning = warning[..237] + "...";
            return new WingetInventory(Array.Empty<WingetRow>(), string.IsNullOrWhiteSpace(warning) ? "WinGet inventory failed." : warning);
        }
        return new WingetInventory(rows, null);
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

            Match match = Regex.Match(show.Output, @"(?m)^\s*Found\s+.+?\s+\[([^\]]+)\]\s*$");
            if (match.Success)
            {
                string id = match.Groups[1].Value.Trim();
                if (id.Length > 0 && !id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) && !id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase))
                {
                    _idCache[cacheKey] = id;
                    return id;
                }
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

        string? id = ParseSearchId(result.Output, name);
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
            if (pinVersion && !string.IsNullOrWhiteSpace(app.Version))
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
        foreach (string extra in SplitArguments(app.WingetArgs)) args.Add(extra);

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

        string[] lines = result.Output.Replace("\r", "").Split('\n');
        int headerIndex = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^Name\s+Id\s+Version"));
        if (headerIndex < 0) return null;

        int idPos = lines[headerIndex].IndexOf("Id", StringComparison.Ordinal);
        int versionPos = lines[headerIndex].IndexOf("Version", StringComparison.Ordinal);
        if (idPos <= 0 || versionPos <= idPos) return null;

        foreach (string line in lines.Skip(headerIndex + 1))
        {
            if (line.Length < versionPos || !line.Any(c => !char.IsWhiteSpace(c))) continue;
            string name = line[..idPos].Trim();
            string id = line.Substring(idPos, versionPos - idPos).Trim();
            if (TextUtil.LooksTruncated(id) || id.Length == 0) continue;
            if (TextUtil.NormalizeLoose(name) == TextUtil.NormalizeLoose(candidate))
            {
                return id;
            }
        }
        return null;
    }

    private static string? ParseSearchId(string output, string expectedName)
    {
        string[] lines = output.Replace("\r", "").Split('\n');
        int headerIndex = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^Name\s+Id\s+Version"));
        if (headerIndex < 0) return null;
        int idPos = lines[headerIndex].IndexOf("Id", StringComparison.Ordinal);
        int versionPos = lines[headerIndex].IndexOf("Version", StringComparison.Ordinal);
        if (idPos <= 0 || versionPos <= idPos) return null;

        foreach (string line in lines.Skip(headerIndex + 1))
        {
            if (line.Length < versionPos || !line.Any(c => !char.IsWhiteSpace(c))) continue;
            string name = line[..idPos].Trim();
            string id = line.Substring(idPos, versionPos - idPos).Trim().ToUpperInvariant();
            if (id.Length != 12 || !id.All(c => char.IsAsciiLetterOrDigit(c))) continue;
            if (TextUtil.NormalizeLoose(name) == TextUtil.NormalizeLoose(expectedName)) return id;
        }
        return null;
    }

    private static IReadOnlyList<WingetRow> ParseList(string raw)
    {
        raw = Regex.Replace(raw, @"\x1b\[[0-9;]*[A-Za-z]", "");
        string[] lines = raw.Replace("\r", "").Split('\n');
        int headerIndex = Array.FindIndex(lines, line => Regex.IsMatch(line, @"^Name\s+Id\s+Version"));
        if (headerIndex < 0) return Array.Empty<WingetRow>();

        string header = lines[headerIndex];
        int idPos = header.IndexOf("Id", StringComparison.Ordinal);
        int versionPos = header.IndexOf("Version", StringComparison.Ordinal);
        int availablePos = header.IndexOf("Available", StringComparison.Ordinal);
        int sourcePos = header.IndexOf("Source", StringComparison.Ordinal);
        if (availablePos < 0) availablePos = sourcePos;
        if (sourcePos < 0) sourcePos = header.Length;
        if (idPos <= 0 || versionPos <= idPos) return Array.Empty<WingetRow>();

        var rows = new List<WingetRow>();
        foreach (string line in lines.Skip(headerIndex + 1))
        {
            if (line.Length < versionPos || !line.Any(c => !char.IsWhiteSpace(c))) continue;
            if (line.Length < idPos) continue;
            if (line.TrimStart().StartsWith("-", StringComparison.Ordinal)) continue;

            string name = line[..idPos].Trim();
            string rawId = line.Substring(idPos, versionPos - idPos);
            string rawVersion = availablePos > versionPos
                ? line.Substring(versionPos, availablePos - versionPos)
                : line[versionPos..];
            string source = sourcePos < line.Length ? line[sourcePos..].Trim() : "";
            bool isMsix = rawId.TrimStart().StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);
            bool isArp = rawId.TrimStart().StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase);

            bool idTruncated = TextUtil.LooksTruncated(rawId) || rawId.TrimEnd().Length >= versionPos - idPos - 1;
            bool nameTruncated = TextUtil.LooksTruncated(name) || name.Length >= idPos - 1;
            string id = TextUtil.CleanAscii(rawId);
            string version = TextUtil.CleanAscii(rawVersion);

            if (name.Length == 0) continue;
            if (id.Length > 0 && !isMsix && !isArp && !Regex.IsMatch(id, @"^[A-Za-z0-9._\-]+$"))
            {
                continue;
            }

            rows.Add(new WingetRow(name, idTruncated ? "" : id, version, source, idTruncated, nameTruncated, isMsix, isArp));
        }
        return rows;
    }

    private static IEnumerable<string> Candidates(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(name)) yield return name;

        string withoutVersion = Regex.Replace(name, @"\s+\d[\w.\-]*\s*$", "");
        if (withoutVersion != name && seen.Add(withoutVersion)) yield return withoutVersion;

        string withoutParens = Regex.Replace(name, @"\s*\([^)]*\)", "");
        if (withoutParens != name && seen.Add(withoutParens)) yield return withoutParens;

        string withoutBoth = Regex.Replace(withoutParens, @"\s+\d[\w.\-]*\s*$", "");
        if (withoutBoth != withoutParens && seen.Add(withoutBoth)) yield return withoutBoth;
    }

    private static IEnumerable<string> SplitArguments(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (string part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return part;
        }
    }
}
