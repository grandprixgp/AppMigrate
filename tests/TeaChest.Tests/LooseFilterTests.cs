using TeaChest.Models;

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


    [Theory]
    [InlineData(MatchConfidence.Loose, true)]
    [InlineData(MatchConfidence.Exact, false)]
    [InlineData(MatchConfidence.None, false)]
    public void IsLooseMatchFollowsConfidence(MatchConfidence confidence, bool expected)
        => Assert.Equal(expected, new AppItem { MatchConfidence = confidence }.IsLooseMatch);

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
