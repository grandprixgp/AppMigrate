using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services;

/// <summary>
/// Drops registry entries that only exist to service another app. Words like
/// "Update", "Updater", "Helper" and "Telemetry" don't make an entry noise by
/// themselves: "Dell Command | Update" is an app people open. An entry is only
/// dropped when a same-publisher parent without those words is installed too.
/// </summary>
internal static class RegistryNoiseFilter
{
    private static readonly Regex CompanionWord = new(@"\b(Update|Updater|Helper|Telemetry)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static List<AppItem> Apply(IEnumerable<AppItem> items)
    {
        var all = items.ToList();
        var companionFlags = all.Select(item => CompanionWord.IsMatch(item.Name)).ToArray();
        var parents = all.Where((_, index) => !companionFlags[index]).ToList();

        var kept = new List<AppItem>(all.Count);
        for (int index = 0; index < all.Count; index++)
        {
            if (companionFlags[index] && LooksLikeCompanionOfParent(all[index], parents)) continue;
            kept.Add(all[index]);
        }
        return kept;
    }

    private static bool LooksLikeCompanionOfParent(AppItem item, List<AppItem> parents)
    {
        string publisher = item.Publisher.Trim();
        if (publisher.Length == 0) return false;

        string core = TextUtil.NormalizeStrict(CompanionWord.Replace(item.Name, " "));
        if (core.Length == 0) return false;

        return parents.Any(parent =>
            string.Equals(parent.Publisher.Trim(), publisher, StringComparison.OrdinalIgnoreCase) &&
            TextUtil.NormalizeStrict(parent.Name).StartsWith(core, StringComparison.Ordinal));
    }
}
