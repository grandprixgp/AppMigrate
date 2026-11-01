using System.IO;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.ViewModels;

namespace TeaChest.Tests;

public sealed class InstallStatusTests : IDisposable
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
        public List<InstallProgress> Reports { get; } = new();

        public void Report(InstallProgress value)
        {
            Reports.Add(value);
            MainViewModel.ApplyInstallProgress(value);
        }
    }

    private static AppItem App(string name)
        => new() { Name = name, Id = "Vendor." + name, Source = "winget", InstallType = "winget", IsSelected = true };

    private static InstallPlan Plan(params string[] names)
    {
        var plan = new InstallPlan();
        foreach (string name in names)
        {
            plan.Items.Add(new PlanItem
            {
                Action = "Install",
                Name = name,
                Id = "Vendor." + name,
                Type = "winget",
                App = App(name)
            });
        }
        return plan;
    }

    [Fact]
    public async Task EachOutcomeLandsOnItsOwnRow()
    {
        InstallPlan plan = Plan("Good", "Bad", "Restart");
        plan.Items.Add(new PlanItem { Action = "Manual", Name = "By hand" });

        var service = new InstallService((app, _, _, _) => Task.FromResult(app.Name switch
        {
            "Good" => new ProcessResult(0, "", false),
            "Bad" => new ProcessResult(1, "The installer said no.", false),
            _ => new ProcessResult(3010, "", false)
        }));
        var progress = new InlineProgress();

        InstallReport report = await service.InstallAsync(
            plan, new InstallOptions(), _directory, progress, CancellationToken.None);

        Assert.Equal("Installed", plan.Items[0].Status);
        Assert.Equal("Installed successfully.", plan.Items[0].StatusDetail);
        Assert.Equal("Failed", plan.Items[1].Status);
        Assert.Equal("The installer said no.", plan.Items[1].StatusDetail);
        Assert.Equal("RebootRequired", plan.Items[2].Status);
        Assert.Equal("Installed. Restart the PC to finish.", plan.Items[2].StatusDetail);
        Assert.Equal("", plan.Items[3].Status);

        Assert.Equal(1, report.Installed);
        Assert.Equal(1, report.Failed);
        Assert.Equal(1, report.RebootRequired);
        Assert.Equal(1, report.Manual);
        Assert.Equal(0, report.NotAttempted);

        Assert.True(File.Exists(report.ReportPath));
        Assert.True(File.Exists(report.LogPath));
        Assert.StartsWith(_directory, report.ReportPath, StringComparison.OrdinalIgnoreCase);
        Assert.All(progress.Reports, item => Assert.NotNull(item.Item));
        Assert.Contains(progress.Reports, item => ReferenceEquals(item.Item, plan.Items[0]));
    }

    [Fact]
    public async Task CancelMidListStopsAndLeavesTheRestBlank()
    {
        InstallPlan plan = Plan("First", "Second", "Third");
        using var cts = new CancellationTokenSource();
        var service = new InstallService((app, _, _, _) =>
        {
            if (app.Name == "Second") cts.Cancel();
            return Task.FromResult(new ProcessResult(0, "", false));
        });
        var progress = new InlineProgress();

        InstallReport report = await service.InstallAsync(
            plan, new InstallOptions(), _directory, progress, cts.Token);

        Assert.Equal("Installed", plan.Items[0].Status);
        Assert.Equal("Cancelled", plan.Items[1].Status);
        Assert.Equal("Cancelled by user.", plan.Items[1].StatusDetail);
        Assert.Equal("", plan.Items[2].Status);
        Assert.Equal("", plan.Items[2].StatusDetail);
        Assert.True(report.Cancelled);
        Assert.Equal(1, report.NotAttempted);
        Assert.Equal(2, report.Results.Count);
    }

    [Fact]
    public void PlanItemStartsBlankAndNotifiesOnStatusChanges()
    {
        var item = new PlanItem();
        Assert.Equal("", item.Status);
        Assert.Equal("", item.StatusDetail);

        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.Status = "Installing";
        item.StatusDetail = "Working";
        item.Status = "Installed";

        Assert.Equal(
            new[] { nameof(PlanItem.Status), nameof(PlanItem.StatusDetail), nameof(PlanItem.Status) },
            changed);
    }

    [Fact]
    public void ProgressWithoutARowChangesNothing()
    {
        var item = new PlanItem { Status = "Installed", StatusDetail = "Done" };

        MainViewModel.ApplyInstallProgress(new InstallProgress { Status = "Failed", Detail = "Nope" });
        MainViewModel.ApplyInstallProgress(new InstallProgress { Status = "", Detail = "Nope", Item = item });

        Assert.Equal("Installed", item.Status);
        Assert.Equal("Done", item.StatusDetail);
    }

    [Fact]
    public void OnlyFailedRowsAreRetryable()
    {
        var failed = new PlanItem { Status = "Failed" };
        var cancelled = new PlanItem { Status = "Cancelled" };
        var untouched = new PlanItem();

        Assert.Equal(new[] { failed }, MainViewModel.SelectFailed(new[] { failed, cancelled, untouched }));
    }
}
