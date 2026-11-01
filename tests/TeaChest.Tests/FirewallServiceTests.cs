using TeaChest.Services.Lan;

namespace TeaChest.Tests;

public class FirewallServiceTests
{
    private const string ExePath = @"C:\Users\sam\AppData\Local\TeaChest\TeaChest.exe";

    [Fact]
    public void BuildsFourCommandsInOrder()
    {
        IReadOnlyList<string[]> commands = FirewallService.BuildConfigureCommands(ExePath);

        Assert.Equal(4, commands.Count);
        Assert.Equal(
            new[] { "advfirewall", "firewall", "delete", "rule", "name=TeaChest Nearby" },
            commands[0]);
        Assert.Equal(
            new[] { "advfirewall", "firewall", "delete", "rule", "name=AppMigrate Nearby" },
            commands[1]);
        Assert.Equal(new[] { "advfirewall", "firewall", "add", "rule" }, commands[2].Take(4));
        Assert.Equal(new[] { "advfirewall", "firewall", "add", "rule" }, commands[3].Take(4));
        Assert.Contains("protocol=TCP", commands[2]);
        Assert.Contains("protocol=UDP", commands[3]);
    }

    [Theory]
    [InlineData(2, "TCP")]
    [InlineData(3, "UDP")]
    public void AddsAllowTheExeOnPrivateLocalSubnetTrafficOnly(int index, string protocol)
    {
        string[] add = FirewallService.BuildConfigureCommands(ExePath)[index];

        Assert.Equal(
            new[]
            {
                "advfirewall", "firewall", "add", "rule",
                "name=TeaChest Nearby", "dir=in", "action=allow", $"program={ExePath}",
                "enable=yes", "profile=private", "remoteip=localsubnet", $"protocol={protocol}"
            },
            add);
    }

    [Fact]
    public void PathWithSpacesStaysOneArgument()
    {
        const string path = @"C:\Program Files\TeaChest\TeaChest.exe";

        IReadOnlyList<string[]> commands = FirewallService.BuildConfigureCommands(path);

        Assert.Contains($"program={path}", commands[2]);
        Assert.Contains($"program={path}", commands[3]);
        Assert.DoesNotContain(commands[2], argument => argument.Contains('"', StringComparison.Ordinal));
        Assert.DoesNotContain(commands[3], argument => argument.Contains('"', StringComparison.Ordinal));
    }

    [Fact]
    public void AddCommandsDoNotReuseTheLegacyRuleName()
    {
        IReadOnlyList<string[]> commands = FirewallService.BuildConfigureCommands(ExePath);

        Assert.DoesNotContain("name=AppMigrate Nearby", commands[2]);
        Assert.DoesNotContain("name=AppMigrate Nearby", commands[3]);
    }
}
