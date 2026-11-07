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
