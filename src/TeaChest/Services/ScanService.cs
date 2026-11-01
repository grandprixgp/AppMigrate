using TeaChest.Models;

namespace TeaChest.Services;

public sealed class ScanResult
{
    public IReadOnlyList<AppItem> Apps { get; init; } = Array.Empty<AppItem>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public TimeSpan Duration { get; init; }
}

public sealed class ScanService
{
    private readonly RegistryInventoryService _registry = new();
    private readonly AppxInventoryService _appx = new();
    private readonly WingetService _winget = new();
    private readonly SelectionOptions _options;

    public ScanService(SelectionOptions? options = null)
    {
        _options = options ?? new SelectionOptions();
    }

    public async Task<ScanResult> ScanAsync(IProgress<ScanSnapshot>? progress, CancellationToken cancellationToken)
    {
        var started = DateTime.UtcNow;
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        object gate = new();

        Publish(items, gate, progress, "Starting inventory...", true, 0, false);

        // TODO: more sources! Scoop and Chocolatey exports, VS Code extensions (`code --list-extensions`),
        // global npm/pipx/dotnet tools, and WSL distros. For developers that's half of what makes
        // a PC feel like theirs, and each one is basically another WrapSource call.
        var tasks = new List<Task<SourceBatch>>
        {
            WrapSource("Registry", _registry.ScanAsync(cancellationToken)),
            WrapSource("AppX", _appx.ScanAsync(cancellationToken)),
            WrapWinget(cancellationToken)
        };
        bool wingetAvailable = true;

        while (tasks.Count > 0)
        {
            Task<SourceBatch> finished = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(finished);
            try
            {
                SourceBatch batch = await finished.ConfigureAwait(false);
                lock (gate) MergeRange(items, batch.Items);
                if (batch.Warning != null)
                {
                    warnings.Add(batch.Warning);
                    if (batch.Name == "WinGet" && batch.Items.Count == 0) wingetAvailable = false;
                }
                Publish(items, gate, progress,
                    batch.Warning == null
                        ? $"{batch.Name}: {batch.Items.Count} items · {items.Count} apps so far"
                        : $"{batch.Name} unavailable; continuing with other sources.",
                    true, 0, false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add(ex.Message);
                Publish(items, gate, progress, ex.Message, true, 0, false);
            }
        }

        List<AppItem> resolveTargets = wingetAvailable ? BuildResolutionTargets(items, gate) : new List<AppItem>();
        if (resolveTargets.Count > 0)
        {
            int done = 0;
            Publish(items, gate, progress, $"Resolving {resolveTargets.Count} package IDs in parallel...", true, 0, false);
            // TODO: name lookups are thrown away after every scan. Cache them on disk (keyed by
            // name + publisher) and show the cached answers straight away, but still run the real
            // lookups in the background and patch the results back in, so the cache never goes stale.
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(resolveTargets, parallelOptions, async (item, token) =>
            {
                ResolveResult result = await ResolveOneAsync(item, token).ConfigureAwait(false);
                lock (gate)
                {
                    if (result.WingetId != null)
                    {
                        item.Id = result.WingetId;
                        item.Source = "winget";
                        item.InstallType = "winget";
                        item.Notes = "Matched to a WinGet package by name.";
                    }
                    else if (result.StoreId != null)
                    {
                        item.Id = result.StoreId;
                        item.Source = "msstore";
                        item.InstallType = "msstore";
                        item.Notes = "Matched to a Microsoft Store product ID.";
                    }
                    else if (item.IsWinget)
                    {
                        item.InstallType = "manual";
                        item.Source = "winget";
                        item.Notes = "WinGet reported this app, but its package ID could not be resolved. Reinstall it manually.";
                    }
                    else if (item.IsStore)
                    {
                        item.Notes = "Store app could not be matched automatically. Reinstall it from the Microsoft Store.";
                    }
                    else
                    {
                        item.Notes = "No WinGet package found. Reinstall manually.";
                    }
                    AppClassifier.ApplyClassification(item);
                }

                int current = Interlocked.Increment(ref done);
                if (current == resolveTargets.Count || current % 4 == 0)
                {
                    Publish(items, gate, progress,
                        $"Resolving package IDs... {current}/{resolveTargets.Count}",
                        true,
                        (double)current / resolveTargets.Count,
                        false);
                }
            }).ConfigureAwait(false);
        }

        lock (gate)
        {
            DeduplicateById(items);
            foreach (AppItem item in items.Values)
            {
                if (item.IsWinget && string.IsNullOrWhiteSpace(item.Id))
                {
                    item.InstallType = "manual";
                    item.Source = "winget";
                    item.Notes = "WinGet reported this app, but its package ID could not be resolved. Reinstall it manually.";
                }
                AppClassifier.ApplyClassification(item);
            }
        }

        IReadOnlyList<AppItem> finalApps = Publish(items, gate, progress, "", false, 1, true);

        return new ScanResult
        {
            Apps = finalApps,
            Warnings = warnings,
            Duration = DateTime.UtcNow - started
        };
    }

    private IReadOnlyList<AppItem> Publish(
        Dictionary<string, AppItem> items,
        object gate,
        IProgress<ScanSnapshot>? progress,
        string status,
        bool indeterminate,
        double value,
        bool complete)
    {
        List<AppItem> snapshot;
        lock (gate)
        {
            snapshot = items.Values
                .Select(i => i.Clone())
                .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        foreach (AppItem item in snapshot) AppClassifier.ApplyDefaults(item, _options);

        string finalStatus = complete
            ? $"Scan complete · {snapshot.Count} apps · {snapshot.Count(i => i.IsSelected)} selected"
            : status;

        progress?.Report(new ScanSnapshot
        {
            Apps = snapshot,
            Status = finalStatus,
            IsComplete = complete,
            Progress = value,
            IsIndeterminate = indeterminate
        });
        return snapshot;
    }

    private static Task<SourceBatch> WrapSource(string name, Task<IReadOnlyList<AppItem>> task)
    {
        return task.ContinueWith(t =>
        {
            if (t.IsFaulted) throw t.Exception!.GetBaseException();
            if (t.IsCanceled) throw new OperationCanceledException();
            return new SourceBatch(name, t.Result, null);
        }, TaskScheduler.Default);
    }

    private async Task<SourceBatch> WrapWinget(CancellationToken cancellationToken)
    {
        WingetInventory inventory = await _winget.GetInventoryAsync(cancellationToken).ConfigureAwait(false);
        var mapped = inventory.Rows
            .Where(row => !row.IsMsix)
            .Select(MapWingetRow)
            .ToList();
        return new SourceBatch("WinGet", mapped, inventory.Warning);
    }

    private static AppItem MapWingetRow(WingetRow row)
    {
        string id = row.Id;
        if (row.IsMsix || id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase))
        {
            return new AppItem
            {
                Name = row.Name,
                Id = "",
                Source = "msix",
                InstallType = "manual",
                Version = row.Version,
                PackageFullName = id.Length > 5 ? id[5..] : "",
                Notes = "MSIX package."
            };
        }
        if (row.IsArp || id.StartsWith("ARP\\", StringComparison.OrdinalIgnoreCase))
        {
            return new AppItem
            {
                Name = row.Name,
                Id = id,
                Source = "registry",
                InstallType = "manual",
                Version = row.Version,
                Notes = ""
            };
        }
        if (string.Equals(row.Source, "msstore", StringComparison.OrdinalIgnoreCase) ||
            (id.Length == 12 && id.StartsWith("9", StringComparison.Ordinal) && id.All(char.IsAsciiLetterOrDigit)))
        {
            return new AppItem
            {
                Name = row.Name,
                Id = id,
                Source = "msstore",
                InstallType = "msstore",
                Version = row.Version,
                Notes = id.Length == 0 ? "Microsoft Store package ID was truncated." : ""
            };
        }

        return new AppItem
        {
            Name = row.Name,
            Id = string.IsNullOrWhiteSpace(id) ? "" : id,
            Source = "winget",
            InstallType = "winget",
            Version = row.Version,
            Notes = string.IsNullOrWhiteSpace(id) ? "WinGet package ID was truncated; resolving by name." : ""
        };
    }

    private static void MergeRange(Dictionary<string, AppItem> items, IEnumerable<AppItem> incoming)
    {
        foreach (AppItem item in incoming)
        {
            AppClassifier.ApplyClassification(item);
            string key = TextUtil.NormalizeStrict(item.Name);
            if (string.IsNullOrWhiteSpace(key)) key = TextUtil.NormalizeStrict(item.Id);
            if (string.IsNullOrWhiteSpace(key)) key = Guid.NewGuid().ToString("N");

            if (items.TryGetValue(key, out AppItem? existing))
            {
                existing.MergeFrom(item);
                AppClassifier.ApplyClassification(existing);
            }
            else
            {
                items[key] = item;
            }
        }
    }

    private static List<AppItem> BuildResolutionTargets(Dictionary<string, AppItem> items, object gate)
    {
        lock (gate)
        {
            return items.Values
                .Where(item =>
                {
                    if (AppClassifier.IsInbox(item)) return false;
                    string category = AppClassifier.GetCategory(item.Name, item.InstallType);
                    if (category is "Runtime" or "Driver") return false;

                    if (item.IsWinget && string.IsNullOrWhiteSpace(item.Id)) return true;
                    if (item.IsManual && (item.Source.Equals("registry", StringComparison.OrdinalIgnoreCase) ||
                                          item.Source.Equals("msix", StringComparison.OrdinalIgnoreCase))) return true;
                    return false;
                })
                .ToList();
        }
    }

    private async Task<ResolveResult> ResolveOneAsync(AppItem item, CancellationToken cancellationToken)
    {
        string? wingetId = await _winget.ResolveIdAsync(item.Name, cancellationToken).ConfigureAwait(false);
        if (wingetId != null) return new ResolveResult(wingetId, null);

        if (item.Source.Equals("msix", StringComparison.OrdinalIgnoreCase) && !AppClassifier.IsInbox(item))
        {
            string? storeId = await _winget.ResolveStoreIdAsync(item.Name, cancellationToken).ConfigureAwait(false);
            if (storeId != null) return new ResolveResult(null, storeId);
        }
        return new ResolveResult(null, null);
    }

    private static void DeduplicateById(Dictionary<string, AppItem> items)
    {
        var byId = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AppItem>();
        foreach (AppItem item in items.Values)
        {
            if (!string.IsNullOrWhiteSpace(item.Id) &&
                (item.IsWinget || string.Equals(item.InstallType, "msstore", StringComparison.OrdinalIgnoreCase)))
            {
                string idKey = item.InstallType + "|" + item.Id.ToLowerInvariant();
                if (byId.TryGetValue(idKey, out AppItem? primary))
                {
                    if (item.Name.Length < primary.Name.Length) primary.Name = item.Name;
                    primary.MergeFrom(item);
                    AppClassifier.ApplyClassification(primary);
                    continue;
                }
                byId[idKey] = item;
            }
            result.Add(item);
        }

        items.Clear();
        foreach (AppItem item in result)
        {
            string key = TextUtil.NormalizeStrict(item.Name) + "|" + TextUtil.NormalizeStrict(item.Version);
            if (items.ContainsKey(key)) key += "|" + item.IdentityKey;
            if (items.ContainsKey(key)) key += "|" + Guid.NewGuid().ToString("N");
            items[key] = item;
        }
    }

    private sealed record SourceBatch(string Name, IReadOnlyList<AppItem> Items, string? Warning);
    private sealed record ResolveResult(string? WingetId, string? StoreId);
}
