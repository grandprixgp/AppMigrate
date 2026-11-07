using System.IO;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.ExtraSources;

namespace TeaChest.Tests;

public sealed class ExtraSourceTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("teachest-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FakeSource : IExtraSource
    {
        private readonly Func<CancellationToken, Task<IReadOnlyList<AppItem>>> _scan;

        public FakeSource(string name, Func<CancellationToken, Task<IReadOnlyList<AppItem>>> scan)
        {
            Name = name;
            _scan = scan;
        }

        public string Name { get; }

        public Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken) => _scan(cancellationToken);
    }

    private static FakeSource Npm(params string[] packages) => new("npm", _ =>
        Task.FromResult<IReadOnlyList<AppItem>>(packages
            .Select(p => new AppItem { Name = p, Notes = "npm install -g " + p })
            .ToList()));

    private static Task<ScanService.SourceBatch> Registry(params string[] names)
        => Task.FromResult(new ScanService.SourceBatch("Registry",
            names.Select(n => new AppItem { Name = n, Source = "registry", InstallType = "manual" }).ToList(),
            null));

    private sealed class Run
    {
        public Dictionary<string, AppItem> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Warnings { get; } = new();
        public object Gate { get; } = new();

        public Task<bool> CollectAsync(params Task<ScanService.SourceBatch>[] tasks)
            => ScanService.CollectAsync(tasks.ToList(), Items, Gate, Warnings, _ => { });
    }

    [Fact]
    public async Task FakeSourceMergesIn()
    {
        var run = new Run();

        await run.CollectAsync(ScanService.WrapExtraSource(Npm("typescript"), CancellationToken.None));

        AppItem item = Assert.Single(run.Items.Values);
        Assert.Equal("npm: typescript", item.Name);
        Assert.Equal("npm", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal("npm install -g typescript", item.Notes);
        Assert.Empty(run.Warnings);
    }

    [Fact]
    public async Task SourceNameIsLowerCasedForSource()
    {
        var vscode = new FakeSource("VS Code", _ => Task.FromResult<IReadOnlyList<AppItem>>(
            new[] { new AppItem { Name = "ms-python.python", Notes = "code --install-extension ms-python.python" } }));
        var run = new Run();

        await run.CollectAsync(ScanService.WrapExtraSource(vscode, CancellationToken.None));

        AppItem item = Assert.Single(run.Items.Values);
        Assert.Equal("VS Code: ms-python.python", item.Name);
        Assert.Equal("vs code", item.Source);
    }

    [Fact]
    public async Task NpmPackageCalledGitDoesNotMergeWithGit()
    {
        var run = new Run();

        await run.CollectAsync(
            Registry("Git"),
            ScanService.WrapExtraSource(Npm("git"), CancellationToken.None));

        Assert.Equal(2, run.Items.Count);
        AppItem git = Assert.Single(run.Items.Values, i => i.Name == "Git");
        Assert.Equal("registry", git.Source);
        Assert.Contains(run.Items.Values, i => i.Name == "npm: git" && i.Source == "npm");
    }

    [Fact]
    public async Task FailingSourceDoesNotFailTheScan()
    {
        var broken = new FakeSource("npm", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("npm printed nonsense");
        });
        var run = new Run();

        bool wingetAvailable = await run.CollectAsync(
            Registry("Git"),
            ScanService.WrapExtraSource(broken, CancellationToken.None));

        Assert.True(wingetAvailable);
        Assert.Single(run.Items.Values, i => i.Name == "Git");
        string warning = Assert.Single(run.Warnings);
        Assert.Contains("npm", warning);
        Assert.Contains("npm printed nonsense", warning);
    }

    [Fact]
    public async Task SourceThatThrowsStraightAwayDoesNotFailTheScan()
    {
        var broken = new FakeSource("pipx", _ => throw new InvalidOperationException("boom"));
        var run = new Run();

        await run.CollectAsync(
            Registry("Git"),
            ScanService.WrapExtraSource(broken, CancellationToken.None));

        Assert.Single(run.Items.Values);
        Assert.Equal("pipx couldn't be read: boom", Assert.Single(run.Warnings));
    }

    [Fact]
    public async Task SourceTimingOutOnItsOwnIsJustAWarning()
    {
        var slow = new FakeSource("scoop", _ => throw new TaskCanceledException("timed out"));
        var run = new Run();

        await run.CollectAsync(
            Registry("Git"),
            ScanService.WrapExtraSource(slow, CancellationToken.None));

        Assert.Single(run.Items.Values);
        Assert.Single(run.Warnings);
    }

    [Fact]
    public async Task CancellingTheScanStillCancels()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var waiting = new FakeSource("npm", token =>
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AppItem>>(Array.Empty<AppItem>());
        });
        var run = new Run();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => run.CollectAsync(ScanService.WrapExtraSource(waiting, cts.Token)));
    }

    [Fact]
    public async Task MissingToolGivesAnEmptyListAndNoWarning()
    {
        var missing = new FakeSource("npm", _ => Task.FromResult<IReadOnlyList<AppItem>>(Array.Empty<AppItem>()));
        var run = new Run();

        await run.CollectAsync(ScanService.WrapExtraSource(missing, CancellationToken.None));

        Assert.Empty(run.Items);
        Assert.Empty(run.Warnings);
    }

    [Fact]
    public void PrepareAddsThePrefixOnceAndDropsBlankNames()
    {
        var items = new List<AppItem>
        {
            new() { Name = "npm: eslint" },
            new() { Name = "  prettier " },
            new() { Name = "   " },
            new() { Name = "Git.Git", Id = "Git.Git", Source = "winget", InstallType = "winget", MatchConfidence = MatchConfidence.Loose }
        };

        List<AppItem> prepared = ExtraSourceTools.Prepare(Npm(), items);

        Assert.Equal(new[] { "npm: eslint", "npm: prettier", "npm: Git.Git" }, prepared.Select(i => i.Name));
        Assert.All(prepared, i =>
        {
            Assert.Equal("npm", i.Source);
            Assert.Equal("manual", i.InstallType);
            Assert.Equal(MatchConfidence.None, i.MatchConfidence);
        });
    }

    [Fact]
    public void PrepareCopesWithNoList() => Assert.Empty(ExtraSourceTools.Prepare(Npm(), null));

    [Theory]
    [InlineData("vs code")]
    [InlineData("npm")]
    [InlineData("scoop")]
    public void ApplyDefaultsLeavesAddOnsUnticked(string source)
    {
        var item = new AppItem { Name = "Something: thing", Source = source, InstallType = "manual" };

        AppClassifier.ApplyDefaults(item, new SelectionOptions());

        Assert.False(item.IsSelected);
        Assert.False(item.DefaultSelected);
    }

    [Fact]
    public void ApplyDefaultsLeavesAddOnsUntickedEvenWithIncludeAll()
    {
        var item = new AppItem { Name = "npm: typescript", Source = "npm", InstallType = "manual" };

        AppClassifier.ApplyDefaults(item, new SelectionOptions { IncludeAll = true });

        Assert.False(item.IsSelected);
    }

    [Fact]
    public void ApplyDefaultsStillTicksOrdinaryApps()
    {
        var item = new AppItem { Name = "Obsidian", Source = "registry", InstallType = "manual" };

        AppClassifier.ApplyDefaults(item, new SelectionOptions());

        Assert.True(item.IsSelected);
    }

    [Fact]
    public async Task AddOnsAreNotSentToWingetLookups()
    {
        var run = new Run();
        await run.CollectAsync(
            Registry("Obsidian"),
            ScanService.WrapExtraSource(Npm("typescript", "git"), CancellationToken.None));
        // Even a row that somehow claims to be WinGet with no ID stays out if it came from an add-on source.
        run.Items["odd"] = new AppItem { Name = "VS Code: odd.one", Source = "vs code", InstallType = "winget" };

        List<AppItem> targets = ScanService.BuildResolutionTargets(run.Items, run.Gate);

        AppItem target = Assert.Single(targets);
        Assert.Equal("Obsidian", target.Name);
    }

    [Theory]
    [InlineData("--list-extensions")]
    [InlineData("--depth=0")]
    [InlineData("ls")]
    public void PlainConstantsAreSafe(string token) => Assert.True(ExtraSourceTools.IsSafeToken(token));

    [Theory]
    [InlineData("")]
    [InlineData("a b")]
    [InlineData("x&calc")]
    [InlineData("x|more")]
    [InlineData("%PATH%")]
    [InlineData("\"quoted\"")]
    [InlineData("a^b")]
    [InlineData("out>file")]
    public async Task ShimRefusesAnythingCmdWouldInterpret(string token)
    {
        Assert.False(ExtraSourceTools.IsSafeToken(token));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ExtraSourceTools.RunShimAsync("echo", new[] { token }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(
            () => ExtraSourceTools.RunShimAsync(token, Array.Empty<string>(), CancellationToken.None));
    }

    [Fact]
    public async Task ShimRunsThroughCmd()
    {
        // where.exe is read-only and always there; asking it for cmd proves the arguments arrive.
        ProcessResult result = await ExtraSourceTools.RunShimAsync("where", new[] { "cmd" }, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("cmd.exe", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.False(ExtraSourceTools.IsMissingTool(result));
    }

    [Fact]
    public void FindOnPathTriesEachExtension()
    {
        string shim = Path.Combine(_directory, "faketool.cmd");
        File.WriteAllText(shim, "@echo off");

        // Windows paths ignore case, and the extension comes back as PATHEXT spells it.
        Assert.Equal(shim, ExtraSourceTools.FindOnPath("faketool", @"C:\nowhere-teachest;""" + _directory + @"""", ".EXE;.CMD"), ignoreCase: true);
        Assert.Equal(shim, ExtraSourceTools.FindOnPath("faketool", _directory, null), ignoreCase: true);
        Assert.Equal(shim, ExtraSourceTools.FindOnPath("faketool.cmd", _directory, ".EXE"), ignoreCase: true);
        Assert.Null(ExtraSourceTools.FindOnPath("faketool", _directory, ".EXE"));
        Assert.Null(ExtraSourceTools.FindOnPath("othertool", _directory, ".CMD"));
        Assert.Null(ExtraSourceTools.FindOnPath("faketool", null, ".CMD"));
    }

    [Fact]
    public async Task MissingShimLooksLikeAProgramThatCouldNotStart()
    {
        ProcessResult result = await ExtraSourceTools.RunShimAsync("teachest-no-such-tool-7f3a", Array.Empty<string>(), CancellationToken.None);

        Assert.Equal(-1, result.ExitCode);
        Assert.True(ExtraSourceTools.IsMissingTool(result));
    }

    [Fact]
    public void TimeoutIsThirtySeconds() => Assert.Equal(TimeSpan.FromSeconds(30), ExtraSourceTools.ShimTimeout);
}
