using System.IO;
using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>
/// Something beyond the registry, AppX, and WinGet that knows what a developer has installed,
/// such as VS Code extensions or global npm tools.
/// </summary>
internal interface IExtraSource
{
    /// <summary>Shown as the row prefix ("npm: typescript"). Lower-cased, it becomes the row's Source.</summary>
    string Name { get; }

    /// <summary>
    /// Lists what this source has installed. A missing tool is normal, not an error: return an
    /// empty list and no warning. Each item's Notes should hold the exact command that reinstalls it.
    /// </summary>
    Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken);
}

internal static class ExtraSourceTools
{
    internal static readonly TimeSpan ShimTimeout = TimeSpan.FromSeconds(30);

    // What older cmd.exe builds return for "is not recognized as an internal or external command".
    // Kept for the rare tool that vanishes between the PATH check and the run.
    private const int CommandNotFound = 9009;

    // Everything here goes through cmd.exe, so anything it treats specially (& | < > ^ % " and
    // spaces among them) is refused outright rather than escaped.
    private static readonly Regex SafeToken = new(@"^[A-Za-z0-9._\-/=:@]+$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Runs a .cmd or .ps1 shim such as npm or code, which can't be started directly with
    /// UseShellExecute off. Arguments must be constants written in the source, never scanned data.
    /// A tool that isn't installed comes back with exit code -1, the same as ProcessRunner gives
    /// for a program it can't start.
    /// </summary>
    internal static async Task<ProcessResult> RunShimAsync(string command, string[] args, CancellationToken cancellationToken)
    {
        if (!IsSafeToken(command)) throw new ArgumentException("Shim command must be a plain name.", nameof(command));
        foreach (string arg in args)
        {
            if (!IsSafeToken(arg)) throw new ArgumentException($"Shim argument '{arg}' isn't a plain constant.", nameof(args));
        }

        // cmd.exe's exit code for a missing command isn't the same on every Windows build, so look
        // first and don't start anything when the tool isn't there.
        if (FindOnPath(command, Environment.GetEnvironmentVariable("PATH"), Environment.GetEnvironmentVariable("PATHEXT")) == null)
        {
            return new ProcessResult(-1, command + " isn't installed.", false);
        }

        // The full path, so a cmd.exe in the current folder can't stand in for it. /d skips any
        // AutoRun commands the user has set up for interactive prompts.
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var arguments = new List<string> { "/d", "/c", command };
        arguments.AddRange(args);

        ProcessResult result = await ProcessRunner.RunAsync(cmd, arguments, null, ShimTimeout, cancellationToken).ConfigureAwait(false);
        return result.ExitCode == CommandNotFound ? result with { ExitCode = -1 } : result;
    }

    /// <summary>Finds a command the way cmd.exe would on PATH, trying each PATHEXT extension.</summary>
    internal static string? FindOnPath(string command, string? path, string? pathExt)
    {
        string[] extensions = string.IsNullOrWhiteSpace(pathExt)
            ? new[] { ".COM", ".EXE", ".BAT", ".CMD" }
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasExtension = Path.HasExtension(command);

        foreach (string raw in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = raw.Trim('"');
            if (directory.Length == 0) continue;
            try
            {
                if (hasExtension && File.Exists(Path.Combine(directory, command))) return Path.Combine(directory, command);
                foreach (string extension in extensions)
                {
                    string candidate = Path.Combine(directory, command + extension);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry just isn't a place the tool can be.
            }
        }
        return null;
    }

    /// <summary>True when the tool simply isn't there, which a source treats as "nothing installed".</summary>
    internal static bool IsMissingTool(ProcessResult result) => result.ExitCode == -1 && !result.TimedOut;

    internal static bool IsSafeToken(string? value) => !string.IsNullOrEmpty(value) && SafeToken.IsMatch(value);

    /// <summary>The "npm: " in "npm: typescript".</summary>
    internal static string Prefix(IExtraSource source) => source.Name + ": ";

    /// <summary>
    /// Makes every source's rows look the same, so no source has to remember the rules. The
    /// prefix keeps MergeRange, which merges by name, from folding an npm package called git
    /// into Git itself.
    /// </summary>
    internal static List<AppItem> Prepare(IExtraSource source, IReadOnlyList<AppItem>? items)
    {
        var prepared = new List<AppItem>();
        if (items == null) return prepared;

        string prefix = Prefix(source);
        string sourceKey = source.Name.ToLowerInvariant();
        foreach (AppItem item in items)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Name)) continue;
            string name = item.Name.Trim();
            item.Name = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? name : prefix + name;
            // Manual because TeaChest doesn't run these installers; the note says how to.
            item.InstallType = "manual";
            item.Source = sourceKey;
            item.MatchConfidence = MatchConfidence.None;
            prepared.Add(item);
        }
        return prepared;
    }
}
