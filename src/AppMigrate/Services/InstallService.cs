using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using AppMigrate.Models;

namespace AppMigrate.Services;

public sealed class InstallOptions
{
    public bool Force { get; set; }
    public bool Interactive { get; set; }
    public bool SkipStore { get; set; }
    public bool MatchScope { get; set; }
    public bool PinVersions { get; set; }
}

public sealed class InstallPlan
{
    public List<PlanItem> Items { get; } = new();
    public string? Warning { get; set; }
    public int InstallCount => Items.Count(i => i.Action == "Install");
    public int SkipCount => Items.Count(i => i.Action == "Skip");
    public int ManualCount => Items.Count(i => i.Action == "Manual");
}

public sealed class InstallResultRecord
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Code { get; set; } = "";
    public double Seconds { get; set; }
    public string Output { get; set; } = "";
}

public sealed class InstallReport
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public string Machine { get; set; } = Environment.MachineName;
    public int Installed { get; set; }
    public int AlreadyInstalled { get; set; }
    public int Manual { get; set; }
    public int Failed { get; set; }
    public List<InstallResultRecord> Results { get; set; } = new();
    public string LogPath { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string ManualActionsPath { get; set; } = "";
}

public sealed class InstallService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly WingetService _winget = new();

    public async Task<InstallPlan> BuildPlanAsync(
        IReadOnlyList<AppItem> apps,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var plan = new InstallPlan();
        InstalledIndex? installed = null;
        try
        {
            installed = await InstalledIndex.BuildAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            plan.Warning = "Could not build the installed-app index; the plan may include already-installed apps. " + ex.Message;
        }

        foreach (AppItem app in apps.Where(a => a.IsSelected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new PlanItem { App = app, Name = app.Name, Id = app.Id, Type = app.InstallType };
            if (app.IsManual)
            {
                item.Action = "Manual";
                item.Detail = string.IsNullOrWhiteSpace(app.Notes) ? "No automatic installer." : app.Notes;
            }
            else if (app.IsDownload)
            {
                item.Action = "Manual";
                item.Detail = "Download entries are not installed by this GUI build. Use the CLI installer for this item.";
            }
            else if (string.IsNullOrWhiteSpace(app.Id))
            {
                item.Action = "Manual";
                item.Detail = "Missing package ID.";
            }
            else if (options.SkipStore && string.Equals(app.InstallType, "msstore", StringComparison.OrdinalIgnoreCase))
            {
                item.Action = "Manual";
                item.Detail = "Microsoft Store installs are skipped.";
            }
            else if (!options.Force && installed != null && installed.Contains(app))
            {
                item.Action = "Skip";
                item.Detail = "Already installed.";
            }
            else
            {
                item.Action = "Install";
                item.Detail = string.Equals(app.InstallType, "msstore", StringComparison.OrdinalIgnoreCase)
                    ? "Microsoft Store package."
                    : "WinGet package.";
            }
            plan.Items.Add(item);
        }
        return plan;
    }

    public async Task<InstallReport> InstallAsync(
        InstallPlan plan,
        InstallOptions options,
        string logDirectory,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(logDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string logPath = Path.Combine(logDirectory, $"install-log-{stamp}.txt");
        string reportPath = Path.Combine(logDirectory, $"install-report-{stamp}.json");
        string manualPath = Path.Combine(logDirectory, $"manual-actions-{stamp}.txt");
        var log = new StringBuilder();
        var results = new List<InstallResultRecord>();
        List<PlanItem> installItems = plan.Items.Where(i => i.Action == "Install").ToList();
        int total = installItems.Count;
        int index = 0;

        foreach (PlanItem item in installItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            AppItem app = item.App;
            var stopwatch = Stopwatch.StartNew();
            var appOutput = new StringBuilder();
            var gate = new object();

            AddLog(log, $"[{index}/{total}] {app.Name} ({app.InstallType})");
            progress?.Report(new InstallProgress
            {
                Current = index,
                Total = total,
                AppName = app.Name,
                Status = "Installing"
            });

            ProcessResult result = await _winget.InstallAsync(
                app,
                options.Force,
                options.Interactive,
                options.MatchScope,
                options.PinVersions,
                cancellationToken,
                line =>
                {
                    lock (gate) appOutput.AppendLine(line);
                    AddLog(log, "  " + line);
                    progress?.Report(new InstallProgress
                    {
                        Current = index,
                        Total = total,
                        AppName = app.Name,
                        Status = "Installing",
                        Line = line
                    });
                }).ConfigureAwait(false);

            stopwatch.Stop();
            string status;
            string detail;
            string code = ExitCode(result.ExitCode);
            if (result.TimedOut)
            {
                status = "Failed";
                detail = "Timed out.";
            }
            else if (LooksAlreadyInstalled(result.Output))
            {
                status = "AlreadyInstalled";
                detail = "Already installed.";
            }
            else if (result.ExitCode == 0)
            {
                status = "Installed";
                detail = "Installed successfully.";
            }
            else if (result.Output.Contains("No package found", StringComparison.OrdinalIgnoreCase) ||
                     result.Output.Contains("No applicable installer", StringComparison.OrdinalIgnoreCase))
            {
                status = "Failed";
                detail = "No matching WinGet installer.";
            }
            else if (result.Output.Contains("hash mismatch", StringComparison.OrdinalIgnoreCase))
            {
                status = "Failed";
                detail = "Installer hash mismatch.";
            }
            else if (result.Output.Contains("requires administrator", StringComparison.OrdinalIgnoreCase) ||
                     result.Output.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
            {
                status = "Failed";
                detail = "Needs elevation. Re-run AppMigrate as administrator.";
            }
            else
            {
                status = "Failed";
                detail = LastMeaningfulLine(result.Output, "WinGet returned an error.");
            }

            AddLog(log, $"  => {status}: {detail} ({code})");
            progress?.Report(new InstallProgress
            {
                Current = index,
                Total = total,
                AppName = app.Name,
                Status = status
            });

            results.Add(new InstallResultRecord
            {
                Name = app.Name,
                Id = app.Id,
                Type = app.InstallType,
                Status = status,
                Detail = detail,
                Code = code,
                Seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                Output = Tail(appOutput.ToString(), 12000)
            });
        }

        var report = BuildReport(results, plan, logPath, reportPath, manualPath);
        File.WriteAllText(logPath, log.ToString(), new UTF8Encoding(false));
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

        List<PlanItem> manualItems = plan.Items.Where(i => i.Action == "Manual").ToList();
        if (manualItems.Count > 0 || report.Failed > 0)
        {
            var manual = new StringBuilder();
            manual.AppendLine("Manual actions from AppMigrate");
            manual.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
            manual.AppendLine();
            foreach (PlanItem item in manualItems)
            {
                manual.AppendLine($"- {item.Name} [{item.Type}]");
                manual.AppendLine($"  {item.Detail}");
                if (string.Equals(item.Type, "msstore", StringComparison.OrdinalIgnoreCase) && item.Id.Length == 12)
                {
                    manual.AppendLine($"  ms-windows-store://pdp/?ProductId={item.Id}");
                }
                manual.AppendLine();
            }
            foreach (InstallResultRecord failed in report.Results.Where(r => r.Status == "Failed"))
            {
                manual.AppendLine($"- {failed.Name} [{failed.Type}]");
                manual.AppendLine($"  {failed.Detail}");
                manual.AppendLine();
            }
            File.WriteAllText(manualPath, manual.ToString(), new UTF8Encoding(false));
        }
        else
        {
            manualPath = "";
        }

        report.ManualActionsPath = manualPath;
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return report;
    }

    private static InstallReport BuildReport(List<InstallResultRecord> results, InstallPlan plan, string logPath, string reportPath, string manualPath)
    {
        return new InstallReport
        {
            Installed = results.Count(r => r.Status == "Installed"),
            AlreadyInstalled = results.Count(r => r.Status == "AlreadyInstalled"),
            Manual = plan.ManualCount,
            Failed = results.Count(r => r.Status == "Failed"),
            Results = results,
            LogPath = logPath,
            ReportPath = reportPath,
            ManualActionsPath = manualPath
        };
    }

    private static bool LooksAlreadyInstalled(string output)
        => output.Contains("No newer package versions are available", StringComparison.OrdinalIgnoreCase)
           || output.Contains("No applicable update", StringComparison.OrdinalIgnoreCase)
           || output.Contains("already installed", StringComparison.OrdinalIgnoreCase);

    private static string ExitCode(int code)
        => code >= 0 ? $"0x{code:X8}" : $"0x{(uint)code:X8}";

    private static string LastMeaningfulLine(string output, string fallback)
    {
        string? line = output.Replace("\r", "")
            .Split('\n')
            .Select(s => s.Trim())
            .LastOrDefault(s => s.Length > 0);
        return string.IsNullOrWhiteSpace(line) ? fallback : TextUtil.Shorten(line, 300);
    }

    private static void AddLog(StringBuilder log, string line)
    {
        lock (log)
        {
            log.Append('[').Append(DateTime.Now.ToString("HH:mm:ss")).Append("] ").AppendLine(line);
        }
    }

    private static string Tail(string value, int max)
        => value.Length <= max ? value : value[^max..];
}
