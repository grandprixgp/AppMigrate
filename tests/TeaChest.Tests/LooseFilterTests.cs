using TeaChest.Models;
using TeaChest.ViewModels;

namespace TeaChest.Tests;

public sealed class LooseFilterTests
{
    private const string LooseWingetNote = "Matched to a WinGet package by a loose name match. Worth a quick check.";

    private static AppItem Loose() => new()
    {
        Name = "Zoom Workplace",
        Id = "Zoom.Zoom",
        Source = "winget",
        InstallType = "winget",
        MatchConfidence = MatchConfidence.Loose,
        Notes = LooseWingetNote
    };

    private static AppItem Exact() => new()
    {
        Name = "Git",
        Id = "Git.Git",
        Source = "winget",
        InstallType = "winget",
        MatchConfidence = MatchConfidence.Exact
    };

    [Theory]
    [InlineData(MatchConfidence.Loose, true)]
    [InlineData(MatchConfidence.Exact, false)]
    [InlineData(MatchConfidence.None, false)]
    public void IsLooseMatchFollowsConfidence(MatchConfidence confidence, bool expected)
        => Assert.Equal(expected, new AppItem { MatchConfidence = confidence }.IsLooseMatch);

    [Fact]
    public void WorthCheckingKeepsOnlyLooseMatches()
    {
        Assert.True(MainViewModel.Matches(Loose(), AppFilter.WorthChecking, ""));
        Assert.False(MainViewModel.Matches(Exact(), AppFilter.WorthChecking, ""));
        Assert.False(MainViewModel.Matches(new AppItem { Name = "Paint.NET" }, AppFilter.WorthChecking, ""));
    }

    [Fact]
    public void WorthCheckingStillHonoursSearch()
    {
        Assert.True(MainViewModel.Matches(Loose(), AppFilter.WorthChecking, "  zoom "));
        Assert.False(MainViewModel.Matches(Loose(), AppFilter.WorthChecking, "git"));
    }

    [Fact]
    public void AllShowsEverythingWhenSearchIsBlank()
    {
        Assert.True(MainViewModel.Matches(Loose(), AppFilter.All, ""));
        Assert.True(MainViewModel.Matches(Exact(), AppFilter.All, "   "));
    }

    [Fact]
    public void ExistingFiltersStillWork()
    {
        var manual = new AppItem { Name = "Old Tool", InstallType = "manual", IsSelected = true };
        var store = new AppItem { Name = "Calculator", InstallType = "msstore", Id = "9WZDNCRFHVN5" };

        Assert.True(MainViewModel.Matches(manual, AppFilter.Selected, ""));
        Assert.False(MainViewModel.Matches(manual, AppFilter.NotSelected, ""));
        Assert.True(MainViewModel.Matches(manual, AppFilter.Manual, ""));
        Assert.False(MainViewModel.Matches(manual, AppFilter.Winget, ""));
        Assert.True(MainViewModel.Matches(store, AppFilter.Store, ""));
        Assert.False(MainViewModel.Matches(store, AppFilter.Manual, ""));
        Assert.True(MainViewModel.Matches(Exact(), AppFilter.Winget, ""));
    }

    [Fact]
    public void SearchMatchesTheNoteToo()
        => Assert.True(MainViewModel.Matches(Loose(), AppFilter.All, "quick check"));

    [Theory]
    [InlineData(0, "Worth a check")]
    [InlineData(1, "Worth a check (1)")]
    [InlineData(12, "Worth a check (12)")]
    public void LabelShowsTheCountOnlyWhenThereIsOne(int count, string expected)
        => Assert.Equal(expected, MainViewModel.WorthCheckingLabel(count));

    [Fact]
    public void InventoryRowDropsTheLooseNoteItPicksUp()
    {
        // WinGet's own inventory gave the ID, so there is nothing to check.
        var inventory = new AppItem { Name = "Zoom", Id = "Zoom.Zoom", Source = "winget", InstallType = "winget" };

        inventory.MergeFrom(Loose());

        Assert.Equal(MatchConfidence.None, inventory.MatchConfidence);
        Assert.False(inventory.IsLooseMatch);
        Assert.Equal("", inventory.Notes);
    }

    [Fact]
    public void LooseRowDropsItsNoteWhenTheInventoryRowArrives()
    {
        AppItem loose = Loose();

        loose.MergeFrom(new AppItem { Name = "Zoom", Id = "Zoom.Zoom", Source = "winget", InstallType = "winget" });

        Assert.Equal(MatchConfidence.None, loose.MatchConfidence);
        Assert.False(loose.IsLooseMatch);
        Assert.Equal("", loose.Notes);
    }

    [Fact]
    public void LooseRowTakesTheOtherNoteWhenItStillHolds()
    {
        AppItem loose = Loose();

        loose.MergeFrom(new AppItem { Name = "Zoom", Id = "Zoom.Zoom", InstallType = "winget", Notes = "Installed for this user only." });

        Assert.Equal(MatchConfidence.None, loose.MatchConfidence);
        Assert.Equal("Installed for this user only.", loose.Notes);
    }

    [Fact]
    public void LooseRowKeepsItsNoteWhenItStaysLoose()
    {
        AppItem loose = Loose();

        loose.MergeFrom(Loose());

        Assert.Equal(MatchConfidence.Loose, loose.MatchConfidence);
        Assert.Equal(LooseWingetNote, loose.Notes);
    }

    [Fact]
    public void OtherNotesSurviveAMergeAtNone()
    {
        var item = new AppItem { Name = "Zoom", Id = "Zoom.Zoom", Notes = "Installed for this user only." };

        item.MergeFrom(Loose());

        Assert.Equal("Installed for this user only.", item.Notes);
    }
}
