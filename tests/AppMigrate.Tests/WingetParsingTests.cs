using AppMigrate.Services;

namespace AppMigrate.Tests;

public class WingetParsingTests
{
    private static readonly string EnglishList = WingetFixture.Table(
        new[] { "Name", "Id", "Version", "Available", "Source" },
        new[] { "Git", "Git.Git", "2.45.1", "2.46.0", "winget" },
        new[] { "simplewall", "Henry++.simplewall", "3.8.7", "", "winget" },
        new[] { "Apple Devices", "9NP83LWLPZ9K", "1.1792.39682.0", "", "msstore" },
        new[] { "Legacy Tool 2.0", "ARP\\Machine\\X64\\{E93A9439-3904-40D2-9B06-16B00B71684D}", "2.0", "", "" },
        new[] { "AV1 Video Extension", "MSIX\\Microsoft.AV1VideoExtension_2.0.7.0_x64__8wekyb3d8bbwe", "2.0.7.0", "", "" });

    [Fact]
    public void ParsesEnglishList()
    {
        var rows = WingetService.ParseList(EnglishList);

        Assert.Equal(5, rows.Count);
        Assert.Equal(("Git", "Git.Git", "2.45.1", "winget"), (rows[0].Name, rows[0].Id, rows[0].Version, rows[0].Source));
        Assert.Equal("msstore", rows[2].Source);
        Assert.True(rows[3].IsArp);
        Assert.True(rows[4].IsMsix);
    }

    [Fact]
    public void KeepsIdsContainingPlus()
    {
        var rows = WingetService.ParseList(EnglishList);
        Assert.Contains(rows, r => r.Id == "Henry++.simplewall");
    }

    [Fact]
    public void ParsesGermanListWithoutEnglishHeaders()
    {
        string german = WingetFixture.Table(
            new[] { "Name", "ID", "Version", "Verfügbar", "Quelle" },
            new[] { "Mozilla Firefox (x64 de)", "Mozilla.Firefox.de", "131.0", "132.0", "winget" },
            new[] { "7-Zip 24.08 (x64)", "7zip.7zip", "24.08", "", "winget" });

        var rows = WingetService.ParseList(german);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Mozilla.Firefox.de", rows[0].Id);
        Assert.Equal("131.0", rows[0].Version);
        Assert.Equal("winget", rows[1].Source);
    }

    [Fact]
    public void ParsesListWithoutAvailableColumn()
    {
        string table = WingetFixture.Table(
            new[] { "Name", "Id", "Version", "Source" },
            new[] { "Git", "Git.Git", "2.45.1", "winget" },
            new[] { "Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.6.9", "winget" });

        var rows = WingetService.ParseList(table);

        Assert.Equal("winget", rows[0].Source);
        Assert.Equal("Notepad++.Notepad++", rows[1].Id);
    }

    [Fact]
    public void KeepsColumnsAlignedAfterWideCharacters()
    {
        string japanese = WingetFixture.Table(
            new[] { "名前", "ID", "バージョン", "ソース" },
            new[] { "メモ帳プラスプラス", "Notepad++.Notepad++", "8.6.9", "winget" },
            new[] { "Git", "Git.Git", "2.45.1", "winget" });

        var rows = WingetService.ParseList(japanese);

        Assert.Equal(2, rows.Count);
        Assert.Equal("メモ帳プラスプラス", rows[0].Name);
        Assert.Equal("Notepad++.Notepad++", rows[0].Id);
        Assert.Equal("8.6.9", rows[0].Version);
    }

    // Real header strings from winget-cli's Localization/Resources/ko-KR/winget.resw. Korean is the
    // only shipped language with spaces inside column titles ("장치 ID", "사용 가능").
    [Fact]
    public void ParsesKoreanListWithMultiWordHeaders()
    {
        string korean = WingetFixture.Table(
            new[] { "이름", "장치 ID", "버전", "사용 가능", "원본" },
            new[] { "메모장++", "Notepad++.Notepad++", "8.6.9", "8.7.1", "winget" },
            new[] { "Git", "Git.Git", "2.45.1", "", "winget" });

        var rows = WingetService.ParseList(korean);

        Assert.Equal(2, rows.Count);
        Assert.Equal(("메모장++", "Notepad++.Notepad++", "8.6.9", "winget"), (rows[0].Name, rows[0].Id, rows[0].Version, rows[0].Source));
        Assert.Equal("Git.Git", rows[1].Id);
    }

    [Fact]
    public void KoreanAvailableColumnSplitDoesNotAffectResults()
    {
        // Worst case: every "available" version is too short to rule out the gap inside "사용 가능",
        // so that title gets split in two. Name/Id/Version/Source must still come out right.
        string korean = WingetFixture.Table(
            new[] { "이름", "장치 ID", "버전", "사용 가능", "원본" },
            new[] { "Git", "Git.Git", "2.45", "2.46", "winget" });

        WingetRow row = Assert.Single(WingetService.ParseList(korean));

        Assert.Equal(("Git", "Git.Git", "2.45", "winget"), (row.Name, row.Id, row.Version, row.Source));
    }

    [Fact]
    public void ParsesTraditionalChineseList()
    {
        string chinese = WingetFixture.Table(
            new[] { "名稱", "識別碼", "版本", "可用", "來源" },
            new[] { "記事本++", "Notepad++.Notepad++", "8.6.9", "8.7.1", "winget" });

        WingetRow row = Assert.Single(WingetService.ParseList(chinese));

        Assert.Equal(("記事本++", "Notepad++.Notepad++", "8.6.9", "winget"), (row.Name, row.Id, row.Version, row.Source));
    }

    [Fact]
    public void MultiWordHeaderDoesNotSplitAColumn()
    {
        // Gaps inside a title are ruled out by rows whose text runs through them. With a single
        // short row this is ambiguous, but real inventories have plenty of rows.
        string table = WingetFixture.Table(
            new[] { "Nom du paquet", "ID", "Version", "Source" },
            new[] { "Visual Studio Code", "Microsoft.VisualStudioCode", "1.95.0", "winget" },
            new[] { "Notepad++", "Notepad++.Notepad++", "8.6.9", "winget" });

        WingetTable? parsed = WingetTable.Parse(table);

        Assert.NotNull(parsed);
        Assert.Equal(4, parsed!.ColumnCount);
        Assert.Equal("Nom du paquet", parsed.Headers[0]);
        Assert.Equal("Visual Studio Code", parsed.Rows[0][0]);
    }

    [Fact]
    public void IgnoresSpinnerAndFooter()
    {
        string output = "   - \r   \\ \r" + EnglishList + "\r\n2 packages have version numbers that cannot be determined.\r\n";
        var rows = WingetService.ParseList(output);
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void FlagsTruncatedIdsAndKeepsPrefix()
    {
        string table = WingetFixture.Table(
            new[] { "Name", "Id", "Version", "Source" },
            new[] { "Visual Studio Code - Insiders", "Microsoft.VisualStudioCode.Ins…", "1.96.0", "winget" });

        WingetRow row = Assert.Single(WingetService.ParseList(table));

        Assert.True(row.IdTruncated);
        Assert.Equal("", row.Id);
        Assert.Equal("Microsoft.VisualStudioCode.Ins", row.IdPrefix);
    }

    [Fact]
    public void RepairsTruncatedIdFromExportWhenUnambiguous()
    {
        var rows = new[]
        {
            new WingetRow("VS Code Insiders", "", "1.96", "winget", true, false, false, false, "Microsoft.VisualStudioCode.Ins"),
            new WingetRow("Git", "Git.Git", "2.45", "winget", false, false, false, false)
        };
        var exported = new[]
        {
            new ExportedPackage("Git.Git", "2.45", "winget"),
            new ExportedPackage("Microsoft.VisualStudioCode.Insiders", "1.96", "winget"),
            new ExportedPackage("Microsoft.VisualStudioCode", "1.95", "winget")
        };

        var repaired = WingetService.RepairTruncatedIds(rows, exported);

        Assert.Equal("Microsoft.VisualStudioCode.Insiders", repaired[0].Id);
        Assert.False(repaired[0].IdTruncated);
    }

    [Fact]
    public void LeavesAmbiguousTruncatedIdAlone()
    {
        var rows = new[] { new WingetRow("Python", "", "3.12", "winget", true, false, false, false, "Python.Python.3") };
        var exported = new[]
        {
            new ExportedPackage("Python.Python.3.11", "3.11", "winget"),
            new ExportedPackage("Python.Python.3.12", "3.12", "winget")
        };

        Assert.True(WingetService.RepairTruncatedIds(rows, exported)[0].IdTruncated);
    }

    [Fact]
    public void ParsesExportJson()
    {
        const string json = """
        {
          "Sources": [
            { "Packages": [ { "PackageIdentifier": "Git.Git", "Version": "2.45.1" } ],
              "SourceDetails": { "Name": "winget" } },
            { "Packages": [ { "PackageIdentifier": "9NP83LWLPZ9K" } ],
              "SourceDetails": { "Name": "msstore" } }
          ]
        }
        """;

        var packages = WingetService.ParseExport(json);

        Assert.Equal(new ExportedPackage("Git.Git", "2.45.1", "winget"), packages[0]);
        Assert.Equal(new ExportedPackage("9NP83LWLPZ9K", "", "msstore"), packages[1]);
    }

    [Fact]
    public void SearchPrefersExactVersionedName()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Python 3.13", "Python.Python.3.13", "3.13.0" },
            new[] { "Python 3.12", "Python.Python.3.12", "3.12.7" });

        Assert.Equal("Python.Python.3.12", WingetService.PickSearchId(search, "Python 3.12", storeOnly: false));
    }

    [Fact]
    public void SearchRefusesAmbiguousVersionlessMatch()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Python 3.13", "Python.Python.3.13", "3.13.0" },
            new[] { "Python 3.12", "Python.Python.3.12", "3.12.7" });

        Assert.Null(WingetService.PickSearchId(search, "Python", storeOnly: false));
    }

    [Fact]
    public void SearchAcceptsSingleLooseMatch()
    {
        string search = WingetFixture.Table(
            new[] { "Name", "Id", "Version" },
            new[] { "Everything 1.4", "voidtools.Everything", "1.4.1.1026" });

        Assert.Equal("voidtools.Everything", WingetService.PickSearchId(search, "Everything", storeOnly: false));
    }

    [Theory]
    [InlineData("Found Git [Git.Git]\r\nVersion: 2.45.1\r\n", "Git.Git")]
    [InlineData("Gefunden Git [Git.Git]\r\nVersion: 2.45.1\r\n", "Git.Git")]
    [InlineData("找到 Git [Git.Git]\r\n版本: 2.45.1\r\n", "Git.Git")]
    public void ShowIdIsLocaleIndependent(string output, string expected)
        => Assert.Equal(expected, WingetService.ParseShowId(output));

    [Fact]
    public void ShowWithMultipleMatchesReturnsNull()
    {
        string output = "Multiple packages found matching input criteria. Please refine the input.\r\n" +
                        WingetFixture.Table(new[] { "Name", "Id", "Source" }, new[] { "Git", "Git.Git", "winget" });
        Assert.Null(WingetService.ParseShowId(output));
    }

    [Fact]
    public void CandidatesTrimVersionsStepByStep()
    {
        var candidates = WingetService.Candidates("Python 3.12.1 (64-bit)").ToList();
        Assert.Equal(new[] { "Python 3.12.1 (64-bit)", "Python 3.12.1", "Python 3.12", "Python" }, candidates);
    }
}
