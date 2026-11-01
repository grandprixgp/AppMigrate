using System.IO;
using System.Text;
using System.Text.Json;
using TeaChest.Models;

namespace TeaChest.Services;

/// <summary>
/// Remembers what a name lookup found, keyed by name and publisher, so a rescan can show
/// answers straight away. Every scan still runs the real lookup; the cache only shortens the wait.
/// </summary>
internal sealed class NameCache
{
    internal const int Capacity = 5_000;
    internal const string FileName = "name-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new MatchConfidenceJsonConverter() }
    };

    private readonly string _path;
    private readonly Dictionary<string, NameCacheEntry> _entries;
    private readonly object _gate = new();

    internal NameCache(string directory)
    {
        _path = Path.Combine(directory, FileName);
        _entries = Load(_path);
    }

    internal int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    internal NameCacheEntry? Find(string name, string publisher)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(Key(name, publisher), out NameCacheEntry? entry) ? entry : null;
        }
    }

    /// <summary>Records an answer, including a miss (both IDs null) so it isn't looked up from scratch next time.</summary>
    internal void Set(string name, string publisher, string? wingetId, string? storeId, MatchConfidence confidence)
    {
        string? winget = PackageIds.IsWingetId(wingetId) ? wingetId : null;
        string? store = PackageIds.IsStoreId(storeId) ? storeId!.ToUpperInvariant() : null;
        if (winget == null && store == null) confidence = MatchConfidence.None;

        var entry = new NameCacheEntry(winget, store, confidence, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _entries[Key(name, publisher)] = entry;
            Trim();
        }
    }

    internal void Save()
    {
        string temp = _path + ".tmp";
        try
        {
            Dictionary<string, NameCacheEntry> entries;
            lock (_gate)
            {
                Trim();
                entries = new Dictionary<string, NameCacheEntry>(_entries, StringComparer.Ordinal);
            }

            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            string json = JsonSerializer.Serialize(new CacheFile { Entries = entries }, JsonOptions);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch
        {
            // The cache is a convenience. A full disk or a locked file shouldn't fail a scan.
            TryDelete(temp);
        }
    }

    private static string Key(string name, string publisher)
        => TextUtil.NormalizeStrict(name) + "|" + (publisher ?? "").Trim().ToLowerInvariant();

    private static Dictionary<string, NameCacheEntry> Load(string path)
    {
        var entries = new Dictionary<string, NameCacheEntry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path)) return entries;
            CacheFile? file = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path), JsonOptions);
            if (file?.Entries == null) return entries;

            foreach ((string key, NameCacheEntry? entry) in file.Entries)
            {
                NameCacheEntry? clean = Clean(entry);
                if (clean != null) entries[key] = clean;
            }
            Trim(entries);
        }
        catch
        {
            // A half-written or hand-edited file just means starting over.
            entries.Clear();
        }
        return entries;
    }

    /// <summary>
    /// The file sits in the user's profile, so treat it as untrusted input: an entry whose IDs
    /// don't validate is dropped rather than handed to the installer.
    /// </summary>
    private static NameCacheEntry? Clean(NameCacheEntry? entry)
    {
        if (entry == null) return null;

        string? winget = string.IsNullOrWhiteSpace(entry.WingetId) ? null : entry.WingetId;
        if (winget != null && !PackageIds.IsWingetId(winget)) return null;

        string? store = string.IsNullOrWhiteSpace(entry.StoreId) ? null : entry.StoreId.ToUpperInvariant();
        if (store != null && !PackageIds.IsStoreId(store)) return null;

        MatchConfidence confidence = winget != null || store != null ? entry.Confidence : MatchConfidence.None;
        return new NameCacheEntry(winget, store, confidence, entry.LastCheckedUtc);
    }

    private void Trim() => Trim(_entries);

    private static void Trim(Dictionary<string, NameCacheEntry> entries)
    {
        if (entries.Count <= Capacity) return;
        int surplus = entries.Count - Capacity;
        foreach (string key in entries.OrderBy(pair => pair.Value.LastCheckedUtc).Take(surplus).Select(pair => pair.Key).ToList())
        {
            entries.Remove(key);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private sealed class CacheFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, NameCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
    }
}

/// <summary>One remembered answer. Both IDs null means the lookup found nothing.</summary>
internal sealed record NameCacheEntry(
    string? WingetId,
    string? StoreId,
    MatchConfidence Confidence,
    DateTimeOffset LastCheckedUtc);
