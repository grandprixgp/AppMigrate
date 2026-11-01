using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

public class RegistryNoiseFilterTests
{
    private static AppItem Item(string name, string publisher) => new() { Name = name, Publisher = publisher };

    private static string[] Names(IEnumerable<AppItem> items) => items.Select(item => item.Name).ToArray();

    [Theory]
    [InlineData("Dell Command | Update", "Dell", "Dell SupportAssist", "Dell", true)]
    [InlineData("Zoom Updater", "Zoom", "Zoom Workplace", "Zoom", false)]
    [InlineData("Google Update Helper", "Google LLC", "Google Chrome", "Google LLC", false)]
    [InlineData("Google Update Helper", "Google LLC", "GOOGLE CHROME", " google llc ", false)]
    [InlineData("Foo Helper", "", "Foo", "", true)]
    [InlineData("Foo Helper", "Acme", "Foo", "Other", true)]
    [InlineData("Foo Update", "Acme", "Foo Helper", "Acme", true)]
    [InlineData("Updater", "Acme", "Anything", "Acme", true)]
    [InlineData("Updates Viewer", "Acme", "Updates", "Acme", true)]
    public void DropsACompanionOnlyWhenItsParentIsInstalled(
        string name, string publisher, string otherName, string otherPublisher, bool expectedKept)
    {
        var items = new[] { Item(name, publisher), Item(otherName, otherPublisher) };

        var kept = Names(RegistryNoiseFilter.Apply(items));

        if (expectedKept)
        {
            Assert.Equal(new[] { name, otherName }, kept);
        }
        else
        {
            Assert.Equal(new[] { otherName }, kept);
        }
    }

    [Fact]
    public void KeepsACompanionWhenNoParentIsInstalled()
    {
        var items = new[] { Item("Acme Telemetry", "Acme") };

        Assert.Equal(new[] { "Acme Telemetry" }, Names(RegistryNoiseFilter.Apply(items)));
    }

    [Fact]
    public void KeepsInputOrder()
    {
        var items = new[]
        {
            Item("Beta", "Acme"),
            Item("Zeta Helper", "Acme"),
            Item("Alpha", "Acme"),
            Item("Zeta Suite", "Acme")
        };

        Assert.Equal(new[] { "Beta", "Alpha", "Zeta Suite" }, Names(RegistryNoiseFilter.Apply(items)));
    }

    [Fact]
    public void DropsDellCommandUpdateWhenConfigureIsInstalled()
    {
        // Both names start with "dellcommand", so the accepted limitation means the
        // update entry looks like a companion. Documented as known and accepted.
        var items = new[] { Item("Dell Command | Update", "Dell"), Item("Dell Command | Configure", "Dell") };

        Assert.Equal(new[] { "Dell Command | Configure" }, Names(RegistryNoiseFilter.Apply(items)));
    }
}
