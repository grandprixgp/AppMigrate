using System.IO;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.ViewModels;

namespace TeaChest.Tests;

public sealed class RetryPlanTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("teachest-tests-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover log handle must not fail the test.
        }
    }

    private sealed class InlineProgress : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => MainViewModel.ApplyInstallProgress(value);
    }

    private static AppItem App(string name)
        => new() { Name = name, Id = "Vendor." + name, Source = "winget", InstallType = "winget", IsSelected = true };

    private static PlanItem Row(string name, string status, string detail = "")
        => new()
        {
            Action = "Install",
            Name = name,
            Id = "Vendor." + name,
            Type = "winget",
            Detail = "WinGet package.",
            App = App(name),
            Status = status,
            StatusDetail = detail
        };

    [Fact]
    public void OnlyFailedRowsGoIntoTheRetry()
    {
        var failed = Row("Failed app", "Failed");
        var cancelled = Row("Cancelled app", "Cancelled");
        var untouched = Row("Untried app", "");
        var installed = Row("Installed app", "Installed");

        InstallPlan plan = MainViewModel.BuildRetryPlan(new[] { failed, cancelled, untouched, installed });

        Assert.Equal(new[] { failed }, plan.Items);
        Assert.Equal(1, plan.InstallCount);
    }

    [Fact]
    public void RetriedRowsAreResetToInstall()
    {
        var item = Row("Zoom", "Failed", "The installer said no.");

        InstallPlan plan = MainViewModel.BuildRetryPlan(new[] { item });

        Assert.Equal("Install", item.Action);
        Assert.Equal("", item.Status);
        Assert.Equal("", item.StatusDetail);
        Assert.Equal("WinGet package.", item.Detail);
        Assert.Same(item, Assert.Single(plan.Items));
    }

    [Fact]
    public void RowsThatAreNotRetriedAreLeftAlone()
    {
        var cancelled = Row("Cancelled app", "Cancelled", "Cancelled by user.");
        var untouched = Row("Untried app", "", "");
        var manual = new PlanItem { Action = "Manual", Name = "By hand", Status = "" };

        MainViewModel.BuildRetryPlan(new[] { cancelled, untouched, manual });

        Assert.Equal("Cancelled", cancelled.Status);
        Assert.Equal("Cancelled by user.", cancelled.StatusDetail);
        Assert.Equal("", untouched.Status);
        Assert.Equal("Manual", manual.Action);
        Assert.Equal("", manual.Status);
    }

    [Fact]
    public void NothingFailedMeansAnEmptyPlan()
    {
        InstallPlan plan = MainViewModel.BuildRetryPlan(new[]
        {
            Row("Installed app", "Installed"),
            Row("Untried app", "")
        });

        Assert.Empty(plan.Items);
        Assert.Equal(0, plan.InstallCount);
    }

    [Fact]
    public void OrderIsKept()
    {
        var first = Row("First", "Failed");
        var second = Row("Second", "Failed");
        var third = Row("Third", "Failed");

        InstallPlan plan = MainViewModel.BuildRetryPlan(new[] { first, third, second });

        Assert.Equal(new[] { first, third, second }, plan.Items);
    }

    [Fact]
    public async Task RetryRunsOnlyTheFailedRowsAndSuccessesLeaveTheList()
    {
        var good = Row("Good", "Failed", "First attempt failed.");
        var bad = Row("Bad", "Failed", "First attempt failed.");
        var manual = new PlanItem { Action = "Manual", Name = "By hand", Status = "" };
        PlanItem[] rows = { good, bad, manual };

        InstallPlan plan = MainViewModel.BuildRetryPlan(rows);
        var service = new InstallService((app, _, _, _) => Task.FromResult(app.Name == "Good"
            ? new ProcessResult(0, "", false)
            : new ProcessResult(1, "Still broken.", false)));

        InstallReport report = await service.InstallAsync(
            plan, new InstallOptions(), _directory, new InlineProgress(), CancellationToken.None);

        Assert.Equal("Installed", good.Status);
        Assert.Equal("Failed", bad.Status);
        Assert.Equal("Still broken.", bad.StatusDetail);
        Assert.Equal("", manual.Status);
        Assert.Equal(new[] { bad }, MainViewModel.SelectFailed(rows));
        Assert.Equal(1, report.Installed);
        Assert.Equal(1, report.Failed);
        Assert.StartsWith(_directory, report.ReportPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(report.ReportPath));
        Assert.True(File.Exists(report.LogPath));
    }
}
