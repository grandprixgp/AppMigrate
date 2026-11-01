using System.IO;
using System.Text;
using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

public sealed class NameCacheTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("teachest-tests-").FullName;

    private string CachePath => Path.Combine(_directory, NameCache.FileName);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void MissingFileMeansAnEmptyCache()
    {
        var cache = new NameCache(_directory);

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Find("Git", "Git"));
        Assert.False(File.Exists(CachePath));
    }

    [Fact]
    public void RememberedAnswerSurvivesAReload()
    {
        var cache = new NameCache(_directory);
        cache.Set("Git", "Git", "Git.Git", null, MatchConfidence.Exact);
        cache.Set("Apple Devices", "", null, "9np83lwlpz9k", MatchConfidence.Loose);
        cache.Save();

        var reloaded = new NameCache(_directory);
        NameCacheEntry? winget = reloaded.Find("  git ", " GIT ");
        NameCacheEntry? store = reloaded.Find("apple devices", "");

        Assert.NotNull(winget);
        Assert.Equal("Git.Git", winget!.WingetId);
        Assert.Null(winget.StoreId);
        Assert.Equal(MatchConfidence.Exact, winget.Confidence);

        Assert.NotNull(store);
        Assert.Null(store!.WingetId);
        Assert.Equal("9NP83LWLPZ9K", store.StoreId);
        Assert.Equal(MatchConfidence.Loose, store.Confidence);
    }

    [Fact]
    public void NegativeAnswerIsRemembered()
    {
        var cache = new NameCache(_directory);
        cache.Set("Nothing Like This", "Acme", null, null, MatchConfidence.Exact);
        cache.Save();

        NameCacheEntry? entry = new NameCache(_directory).Find("Nothing Like This", "Acme");

        Assert.NotNull(entry);
        Assert.Null(entry!.WingetId);
        Assert.Null(entry.StoreId);
        Assert.Equal(MatchConfidence.None, entry.Confidence);
    }

    [Fact]
    public void CorruptFileMeansAnEmptyCache()
    {
        File.WriteAllText(CachePath, "{ this is not json");

        var cache = new NameCache(_directory);

        Assert.Equal(0, cache.Count);
        Assert.Null(cache.Find("Git", ""));
    }

    [Fact]
    public void EntriesWithBadIdsAreIgnoredOnRead()
    {
        File.WriteAllText(CachePath, """
        {
          "version": 1,
          "entries": {
            "bad|": { "wingetId": "--override.x", "storeId": null, "confidence": "Exact", "lastCheckedUtc": "2026-01-01T00:00:00+00:00" },
            "badstore|": { "wingetId": null, "storeId": "9NP83LWLPZ9", "confidence": "Exact", "lastCheckedUtc": "2026-01-01T00:00:00+00:00" },
            "good|": { "wingetId": "Git.Git", "storeId": null, "confidence": "Loose", "lastCheckedUtc": "2026-01-01T00:00:00+00:00" }
          }
        }
        """);

        var cache = new NameCache(_directory);

        Assert.Equal(1, cache.Count);
        Assert.Null(cache.Find("bad", ""));
        Assert.Null(cache.Find("badstore", ""));
        Assert.Equal("Git.Git", cache.Find("good", "")!.WingetId);
    }

    [Fact]
    public void CacheKeepsTheMostRecentFiveThousandEntries()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var sb = new StringBuilder("{\"version\":1,\"entries\":{");
        for (int i = 0; i <= NameCache.Capacity; i++)
        {
            if (i > 0) sb.Append(',');
            string stamp = start.AddMinutes(i).ToString("O");
            sb.Append($"\"app{i}|\":{{\"wingetId\":\"Vendor.App{i}\",\"storeId\":null,\"confidence\":\"Exact\",\"lastCheckedUtc\":\"{stamp}\"}}");
        }
        sb.Append("}}");
        File.WriteAllText(CachePath, sb.ToString());

        var cache = new NameCache(_directory);

        Assert.Equal(NameCache.Capacity, cache.Count);
        Assert.Null(cache.Find("app0", ""));
        Assert.NotNull(cache.Find("app5000", ""));

        cache.Save();
        Assert.Equal(NameCache.Capacity, new NameCache(_directory).Count);
    }

    [Fact]
    public void SavingLeavesNoTemporaryFile()
    {
        var cache = new NameCache(_directory);
        cache.Set("Git", "", "Git.Git", null, MatchConfidence.Exact);

        cache.Save();

        Assert.Equal(new[] { "name-cache.json" }, Directory.GetFiles(_directory).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public void ALaterAnswerReplacesAnEarlierOne()
    {
        var cache = new NameCache(_directory);
        cache.Set("Git", "", null, null, MatchConfidence.None);
        cache.Set("Git", "", "Git.Git", null, MatchConfidence.Exact);

        Assert.Equal("Git.Git", cache.Find("Git", "")!.WingetId);
    }

    [Fact]
    public void CachedWingetMatchLooksLikeAFreshOne()
    {
        var item = new AppItem { Name = "Zoom", Source = "registry", InstallType = "manual" };

        ScanService.ApplyResolution(item, Winget("Zoom.Zoom", MatchConfidence.Exact));

        Assert.Equal("Zoom.Zoom", item.Id);
        Assert.Equal("winget", item.Source);
        Assert.Equal("winget", item.InstallType);
        Assert.Equal(MatchConfidence.Exact, item.MatchConfidence);
        Assert.Equal("Matched to a WinGet package by name.", item.Notes);
    }

    [Fact]
    public void LooseMatchKeepsItsWarning()
    {
        var item = new AppItem { Name = "Everything", Source = "registry", InstallType = "manual" };

        ScanService.ApplyResolution(item, Winget("voidtools.Everything", MatchConfidence.Loose));

        Assert.Equal(MatchConfidence.Loose, item.MatchConfidence);
        Assert.Equal("Matched to a WinGet package by a loose name match. Worth a quick check.", item.Notes);
    }

    [Fact]
    public void StoreMatchLooksLikeAFreshOne()
    {
        var item = new AppItem
        {
            Name = "Apple Devices",
            Id = "MSIX\\AppleInc.AppleDevices",
            Source = "msix",
            InstallType = "manual",
            PackageFamilyName = "AppleInc.AppleDevices"
        };

        ScanService.ApplyResolution(item, new ScanService.ResolveResult(null, new NameMatch("9NP83LWLPZ9K", MatchConfidence.Exact)));

        Assert.Equal("9NP83LWLPZ9K", item.Id);
        Assert.Equal("msstore", item.Source);
        Assert.Equal("msstore", item.InstallType);
        Assert.Equal(MatchConfidence.Exact, item.MatchConfidence);
        Assert.Equal("Matched to a Microsoft Store product ID.", item.Notes);
    }

    [Fact]
    public void WingetMissFallsBackToManual()
    {
        var item = new AppItem { Name = "Truncated", Id = "", Source = "winget", InstallType = "winget" };

        ScanService.ApplyResolution(item, NoMatch);

        Assert.Equal("", item.Id);
        Assert.Equal("winget", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal(MatchConfidence.None, item.MatchConfidence);
        Assert.Equal("WinGet reported this app, but its package ID could not be resolved. Reinstall it manually.", item.Notes);
    }

    [Fact]
    public void StoreMissPutsThePackageBack()
    {
        var item = new AppItem
        {
            Name = "Apple Devices",
            Id = "MSIX\\AppleInc.AppleDevices",
            Source = "msix",
            InstallType = "manual",
            PackageFamilyName = "AppleInc.AppleDevices"
        };

        ScanService.ApplyResolution(item, NoMatch);

        Assert.Equal("MSIX\\AppleInc.AppleDevices", item.Id);
        Assert.Equal("msix", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal(MatchConfidence.None, item.MatchConfidence);
        Assert.Equal("Store app could not be matched automatically. Reinstall it from the Microsoft Store.", item.Notes);
    }

    [Fact]
    public void ManualMissSaysSo()
    {
        var item = new AppItem { Name = "Unknown Tool", Source = "registry", InstallType = "manual" };

        ScanService.ApplyResolution(item, NoMatch);

        Assert.Equal("manual", item.InstallType);
        Assert.Equal("No WinGet package found. Reinstall manually.", item.Notes);
    }

    [Fact]
    public void ARealMissOverridesARememberedWingetMatch()
    {
        var item = new AppItem { Name = "Zoom", Id = "", Source = "winget", InstallType = "winget" };
        ScanService.ApplyResolution(item, Winget("Zoom.Zoom", MatchConfidence.Exact));
        Assert.Equal("Zoom.Zoom", item.Id);

        ScanService.ApplyResolution(item, NoMatch);

        Assert.Equal("", item.Id);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal(MatchConfidence.None, item.MatchConfidence);
        Assert.Contains("could not be resolved", item.Notes);
    }

    [Fact]
    public void ARealMissOverridesARememberedStoreMatch()
    {
        var item = new AppItem
        {
            Name = "Apple Devices",
            Id = "MSIX\\AppleInc.AppleDevices",
            Source = "msix",
            InstallType = "manual",
            PackageFamilyName = "AppleInc.AppleDevices"
        };
        ScanService.ApplyResolution(item, new ScanService.ResolveResult(null, new NameMatch("9NP83LWLPZ9K", MatchConfidence.Exact)));
        Assert.Equal("9NP83LWLPZ9K", item.Id);

        ScanService.ApplyResolution(item, NoMatch);

        Assert.Equal("MSIX\\AppleInc.AppleDevices", item.Id);
        Assert.Equal("msix", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal(MatchConfidence.None, item.MatchConfidence);
    }

    [Fact]
    public void ARealAnswerReplacesADifferentRememberedOne()
    {
        var item = new AppItem { Name = "Zoom", Id = "", Source = "winget", InstallType = "winget" };
        ScanService.ApplyResolution(item, Winget("Other.Zoom", MatchConfidence.Exact));

        ScanService.ApplyResolution(item, Winget("Zoom.Zoom", MatchConfidence.Loose));

        Assert.Equal("Zoom.Zoom", item.Id);
        Assert.Equal(MatchConfidence.Loose, item.MatchConfidence);
    }

    private static ScanService.ResolveResult Winget(string id, MatchConfidence confidence)
        => new(new NameMatch(id, confidence), null);

    private static readonly ScanService.ResolveResult NoMatch = new(null, null);
}
