using System.Text.Json;
using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

public class MatchConfidenceTests
{
    [Theory]
    // Only an Exact finding can be graded down; None and Loose pass through untouched.
    [InlineData("Python 3.12.1", "Python 3.12.1", MatchConfidence.None, MatchConfidence.None)]
    [InlineData("Python 3.12.1", "Python 3.12", MatchConfidence.None, MatchConfidence.None)]
    [InlineData("Python 3.12.1", "Python 3.12.1", MatchConfidence.Loose, MatchConfidence.Loose)]
    [InlineData("Python 3.12.1", "Python 3.12", MatchConfidence.Loose, MatchConfidence.Loose)]
    [InlineData("Python 3.12.1", "Python 3.12.1", MatchConfidence.Exact, MatchConfidence.Exact)]
    [InlineData("Python 3.12.1", "Python 3.12.1 (x64)", MatchConfidence.Exact, MatchConfidence.Exact)]
    [InlineData("Python 3.12.1", "Python 3.12", MatchConfidence.Exact, MatchConfidence.Loose)]
    [InlineData("App 3.12.1 (x64)", "App", MatchConfidence.Exact, MatchConfidence.Loose)]
    public void GradeOnlyTrustsTheAppsOwnName(
        string originalName,
        string candidate,
        MatchConfidence found,
        MatchConfidence expected)
        => Assert.Equal(expected, WingetService.Grade(originalName, candidate, found));

    [Fact]
    public void ExactSearchNameIsExact()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Python 3.13", "Python.Python.3.13", "3.13.0" },
            new[] { "Python 3.12", "Python.Python.3.12", "3.12.7" });

        NameMatch? match = WingetService.PickSearchMatch(search, "Python 3.12", storeOnly: false);

        Assert.Equal(new NameMatch("Python.Python.3.12", MatchConfidence.Exact), match);
    }

    [Fact]
    public void AmbiguousVersionlessMatchIsNoMatch()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Python 3.13", "Python.Python.3.13", "3.13.0" },
            new[] { "Python 3.12", "Python.Python.3.12", "3.12.7" });

        Assert.Null(WingetService.PickSearchMatch(search, "Python", storeOnly: false));
    }

    [Fact]
    public void SingleVersionlessMatchIsLoose()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Everything 1.4", "voidtools.Everything", "1.4.1.1026" });

        NameMatch? match = WingetService.PickSearchMatch(search, "Everything", storeOnly: false);

        Assert.Equal(new NameMatch("voidtools.Everything", MatchConfidence.Loose), match);
    }

    [Fact]
    public void StoreSearchReturnsUpperCasedStoreId()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Apple Devices", "9np83lwlpz9k", "1.1792.39682.0" });

        NameMatch? match = WingetService.PickSearchMatch(search, "Apple Devices", storeOnly: true);

        Assert.Equal(new NameMatch("9NP83LWLPZ9K", MatchConfidence.Exact), match);
    }

    [Fact]
    public void PickSearchIdStillReturnsJustTheId()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Everything 1.4", "voidtools.Everything", "1.4.1.1026" });

        Assert.Equal("voidtools.Everything", WingetService.PickSearchId(search, "Everything", storeOnly: false));
        Assert.Null(WingetService.PickSearchId(search, "Nothing like this", storeOnly: false));
    }

    [Fact]
    public void ManifestRoundTripKeepsConfidence()
    {
        var document = new ManifestDocument
        {
            Apps = new List<AppRecord>
            {
                new() { Name = "Git", Id = "Git.Git", InstallType = "winget", MatchConfidence = MatchConfidence.Loose },
                new() { Name = "By hand", InstallType = "manual" }
            }
        };

        string json = JsonSerializer.Serialize(document, ManifestService.JsonOptions);
        Assert.Contains("\"matchConfidence\": \"Loose\"", json);
        Assert.Contains("\"matchConfidence\": \"None\"", json);

        ManifestDocument? back = JsonSerializer.Deserialize<ManifestDocument>(json, ManifestService.JsonOptions);

        Assert.NotNull(back);
        Assert.Equal(MatchConfidence.Loose, back!.Apps[0].MatchConfidence);
        Assert.Equal(MatchConfidence.Loose, back.Apps[0].ToItem().MatchConfidence);
        Assert.Equal(MatchConfidence.None, back.Apps[1].MatchConfidence);
    }

    [Fact]
    public void ManifestWithoutTheFieldLoadsAsNone()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "apps": [
            { "name": "Git", "id": "Git.Git", "source": "winget", "installType": "winget" }
          ]
        }
        """;

        ManifestDocument? document = JsonSerializer.Deserialize<ManifestDocument>(json, ManifestService.JsonOptions);

        Assert.NotNull(document);
        Assert.Equal(MatchConfidence.None, document!.Apps[0].MatchConfidence);
        Assert.Equal(MatchConfidence.None, document.Apps[0].ToItem().MatchConfidence);
    }

    [Fact]
    public void FromItemAndToItemKeepConfidence()
    {
        var item = new AppItem { Name = "Zoom", Id = "Zoom.Zoom", InstallType = "winget", MatchConfidence = MatchConfidence.Loose };

        AppRecord record = AppRecord.FromItem(item);

        Assert.Equal(MatchConfidence.Loose, record.MatchConfidence);
        Assert.Equal(MatchConfidence.Loose, record.ToItem().MatchConfidence);
    }

    [Fact]
    public void MergeTakesConfidenceWhenThisItemHasNone()
    {
        var item = new AppItem { Name = "Zoom" };

        item.MergeFrom(new AppItem { Name = "Zoom", MatchConfidence = MatchConfidence.Loose });

        Assert.Equal(MatchConfidence.Loose, item.MatchConfidence);
    }

    [Fact]
    public void MergeDoesNotDowngradeConfidence()
    {
        var item = new AppItem { Name = "Zoom", MatchConfidence = MatchConfidence.Exact };

        item.MergeFrom(new AppItem { Name = "Zoom", MatchConfidence = MatchConfidence.Loose });
        Assert.Equal(MatchConfidence.Exact, item.MatchConfidence);

        item.MergeFrom(new AppItem { Name = "Zoom" });
        Assert.Equal(MatchConfidence.Exact, item.MatchConfidence);
    }

    [Fact]
    public void CloneCopiesConfidence()
    {
        var item = new AppItem { Name = "Zoom", MatchConfidence = MatchConfidence.Exact };

        Assert.Equal(MatchConfidence.Exact, item.Clone().MatchConfidence);
        Assert.Equal(MatchConfidence.None, new AppItem().MatchConfidence);
    }
}
