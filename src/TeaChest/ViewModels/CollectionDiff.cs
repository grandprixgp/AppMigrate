using System.Collections.ObjectModel;

namespace TeaChest.ViewModels;

/// <summary>
/// Applies a new list to an observable collection in place, so the grid keeps its scroll
/// position and row instances while the data behind it keeps changing.
/// </summary>
internal static class CollectionDiff
{
    internal static void Apply<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> next,
        Func<T, string> key,
        Func<T, T, bool> sameContent,
        Action<T>? onAdded = null,
        Action<T>? onRemoved = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(sameContent);

        var currentByIdentity = new Dictionary<Identity, int>();
        var currentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < target.Count; i++)
        {
            currentByIdentity[NextIdentity(key(target[i]) ?? "", currentCounts)] = i;
        }

        var desired = new List<T>(next.Count);
        T[] existing = new T[next.Count];
        var changed = new bool[next.Count];
        bool[] kept = new bool[target.Count];
        var desiredCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < next.Count; i++)
        {
            T item = next[i];
            Identity identity = NextIdentity(key(item) ?? "", desiredCounts);
            if (currentByIdentity.TryGetValue(identity, out int index))
            {
                T current = target[index];
                existing[i] = current;
                changed[i] = !sameContent(current, item);
                kept[index] = true;
                desired.Add(changed[i] ? item : current);
            }
            else
            {
                desired.Add(item);
            }
        }

        // Remove from the end so the indices still to be visited stay valid.
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (kept[i]) continue;
            T removed = target[i];
            target.RemoveAt(i);
            onRemoved?.Invoke(removed);
        }

        // A key that stayed put but whose content changed swaps in the fresh instance at the
        // same position. The old instance has left the collection and the new one has arrived,
        // so subscribers move with it even though the row count is unchanged.
        for (int i = 0; i < next.Count; i++)
        {
            if (!changed[i]) continue;
            int index = IndexOf(target, existing[i]);
            if (index < 0) continue;
            target[index] = desired[i];
            onRemoved?.Invoke(existing[i]);
            onAdded?.Invoke(desired[i]);
        }

        // Everything that is still out of order is moved into place; anything missing is added.
        for (int i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && Same(target[i], desired[i])) continue;

            int found = -1;
            for (int j = i + 1; j < target.Count; j++)
            {
                if (Same(target[j], desired[i]))
                {
                    found = j;
                    break;
                }
            }

            if (found >= 0)
            {
                target.Move(found, i);
            }
            else
            {
                target.Insert(i, desired[i]);
                onAdded?.Invoke(desired[i]);
            }
        }
    }

    private static Identity NextIdentity(string key, Dictionary<string, int> counts)
    {
        counts.TryGetValue(key, out int seen);
        counts[key] = seen + 1;
        return new Identity(key, seen);
    }

    private static int IndexOf<T>(ObservableCollection<T> items, T item)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (Same(items[i], item)) return i;
        }
        return -1;
    }

    private static bool Same<T>(T left, T right)
        => ReferenceEquals(left, right) || EqualityComparer<T>.Default.Equals(left, right);

    /// <summary>A key plus how many times that key has already appeared, so duplicates stay apart.</summary>
    private readonly record struct Identity(string Key, int Occurrence);
}
