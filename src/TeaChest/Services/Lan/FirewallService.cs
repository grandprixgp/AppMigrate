using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace TeaChest.Services.Lan;

/// <summary>
/// Manages the one inbound firewall rule the sharing PC needs. Adding it requires elevation, so
/// TeaChest relaunches itself with <c>--configure-firewall</c> through a UAC prompt. The rule is
/// limited to this exe, Private networks, and the local subnet.
/// </summary>
public static class FirewallService
{
    public const string RuleName = "TeaChest Nearby";
    public const string ConfigureArgument = "--configure-firewall";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TeaChest.exe");

    public static async Task<bool> RuleExistsAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "netsh.exe",
            new[] { "advfirewall", "firewall", "show", "rule", $"name={RuleName}", "verbose" },
            null,
            TimeSpan.FromSeconds(15),
            cancellationToken).ConfigureAwait(false);
        // The output is localized, but the program path is not; a moved exe needs a new rule.
        return result.ExitCode == 0 && result.Output.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Adds the rule through an elevated copy of this exe. False if the user declined UAC or it failed.</summary>
    public static async Task<bool> EnsureRuleAsync(CancellationToken cancellationToken)
    {
        if (await RuleExistsAsync(cancellationToken).ConfigureAwait(false)) return true;
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = ConfigureArgument,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process == null) return false;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // ERROR_CANCELLED: the user said no to the UAC prompt.
            return false;
        }
    }

    /// <summary>Runs inside the elevated process. Returns the process exit code.</summary>
    public static int ConfigureElevated()
    {
        RunNetsh("advfirewall", "firewall", "delete", "rule", $"name={RuleName}");
        int tcp = RunNetsh(AddRule("TCP"));
        int udp = RunNetsh(AddRule("UDP"));
        return tcp != 0 ? tcp : udp;
    }

    private static string[] AddRule(string protocol) => new[]
    {
        "advfirewall", "firewall", "add", "rule",
        $"name={RuleName}", "dir=in", "action=allow", $"program={ExePath}",
        "enable=yes", "profile=private", "remoteip=localsubnet", $"protocol={protocol}"
    };

    private static int RunNetsh(params string[] args)
    {
        var startInfo = new ProcessStartInfo("netsh.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string arg in args) startInfo.ArgumentList.Add(arg);
        using Process? process = Process.Start(startInfo);
        if (process == null) return -1;
        process.WaitForExit();
        return process.ExitCode;
    }
}
