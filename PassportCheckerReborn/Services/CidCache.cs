using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace PassportCheckerReborn.Services;

/// <summary>Where a cached name originally came from.</summary>
public enum NameSource
{
    /// <summary>Live game data — PF listing packet or adventure plate (CharaCard).</summary>
    Live,

    /// <summary>Read from the PlayerTrack plugin's database.</summary>
    PlayerTrack,
}

/// <summary>A name a Content ID was previously seen under, before the current one.</summary>
/// <param name="Name">The previous character name.</param>
/// <param name="WorldId">The home-world ID at that time.</param>
/// <param name="WorldName">The home-world name at that time (denormalised for display).</param>
/// <param name="SeenUntil">UTC timestamp of roughly when this name was last valid / replaced.</param>
public sealed record PreviousName(
    string Name,
    ushort WorldId,
    string WorldName,
    DateTime SeenUntil);

/// <summary>
/// A single cached entry mapping a Content ID to a player's last-known name and world.
/// </summary>
/// <param name="Name">The player's last-known character name.</param>
/// <param name="WorldId">The player's last-known home-world ID.</param>
/// <param name="WorldName">The player's last-known home-world name (denormalised for display).</param>
/// <param name="LastSeen">UTC timestamp of when the underlying name observation was made
/// (live = when confirmed; PlayerTrack = when PlayerTrack last saw the player). This is the
/// entry's "data age" and drives stale re-verification.</param>
public sealed record CidCacheEntry(
    string Name,
    ushort WorldId,
    string WorldName,
    DateTime LastSeen)
{
    /// <summary>Origin of this name. Defaults to <see cref="NameSource.Live"/>.</summary>
    public NameSource Source { get; init; } = NameSource.Live;

    /// <summary>Names this Content ID was previously seen under (most recent first). <c>null</c> when none.</summary>
    public IReadOnlyList<PreviousName>? PreviousNames { get; init; }

    /// <summary>UTC time a stale re-verification was last attempted (success or failure), used to throttle retries.</summary>
    public DateTime? LastVerifyAttempt { get; init; }
}

/// <summary>
/// Persistent cache that maps player Content IDs to their last-known name and world.
/// Stored as a JSON file in the plugin configuration directory so resolved names
/// survive plugin reloads and game restarts.
///
/// <para>
/// A player's Content ID is stable even when they change their character name or
/// home world, so cached entries may become stale. The cache is therefore treated as
/// a <em>fallback</em>: any fresh data obtained from PF listing packets or a
/// CharaCard (adventure plate) response always overwrites an existing entry.
/// </para>
/// </summary>
public sealed class CidCache : IDisposable
{
    private readonly string filePath;

    // ConcurrentDictionary: read on the framework/UI thread and written from the background name-resolution
    // task; Save() also enumerates it. Read-modify-writes (Set/MarkVerifyAttempt) aren't atomic across the
    // TryGetValue+assign pair, but a raced merge just loses a history entry — it can never corrupt the map.
    private readonly ConcurrentDictionary<ulong, CidCacheEntry> entries = new();
    private volatile bool dirty;

    /// <summary>Maximum number of previous names retained per entry.</summary>
    private const int MaxPreviousNames = 10;

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public CidCache()
    {
        filePath = Path.Combine(
            PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory(),
            "cid_cache.json");

        Load();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the number of entries currently held in the cache.
    /// </summary>
    public int Count => entries.Count;

    /// <summary>
    /// Attempts to retrieve the cached entry for the given Content ID.
    /// </summary>
    public bool TryGet(ulong contentId, out CidCacheEntry? entry)
        => entries.TryGetValue(contentId, out entry);

    /// <summary>
    /// Writes (or overwrites) the cached entry for the given Content ID with freshly-obtained live
    /// data (PF packet / adventure plate). Always overwrites — live data is the freshest source.
    /// If the name changed, the old name is folded into the entry's <see cref="CidCacheEntry.PreviousNames"/>,
    /// and the stale re-verification back-off is reset.
    /// </summary>
    public void Set(ulong contentId, string name, ushort worldId, string worldName)
    {
        if (contentId == 0 || string.IsNullOrEmpty(name))
        {
            return;
        }

        entries.TryGetValue(contentId, out var existing);
        entries[contentId] = new CidCacheEntry(name, worldId, worldName, DateTime.UtcNow)
        {
            Source = NameSource.Live,
            PreviousNames = MergeHistory(existing, name, null),
            LastVerifyAttempt = null,
        };
        dirty = true;
    }

    /// <summary>
    /// Writes a PlayerTrack-sourced entry (name/world) using PlayerTrack's own last-seen time as the
    /// entry's data age, seeding the name history from PlayerTrack's recorded previous names. Does NOT
    /// overwrite an existing live entry — live data is always preferred over PlayerTrack.
    /// </summary>
    public void SetFromPlayerTrack(
        ulong contentId, string name, ushort worldId, string worldName,
        DateTime playerTrackLastSeen, IReadOnlyList<PreviousName>? seedHistory)
    {
        if (contentId == 0 || string.IsNullOrEmpty(name))
        {
            return;
        }

        entries.TryGetValue(contentId, out var existing);

        // Never downgrade a live entry to PlayerTrack data.
        if (existing is { Source: NameSource.Live })
        {
            return;
        }

        entries[contentId] = new CidCacheEntry(name, worldId, worldName, playerTrackLastSeen)
        {
            Source = NameSource.PlayerTrack,
            PreviousNames = MergeHistory(existing, name, seedHistory),
            LastVerifyAttempt = existing?.LastVerifyAttempt,
        };
        dirty = true;
    }

    /// <summary>
    /// Returns <c>true</c> if the entry for <paramref name="contentId"/> is older than
    /// <paramref name="staleAfter"/> and hasn't had a re-verification attempt within
    /// <paramref name="retryCooldown"/> (so callers don't re-check the same stale name every time).
    /// </summary>
    public bool ShouldReverify(ulong contentId, TimeSpan staleAfter, TimeSpan retryCooldown)
    {
        if (!entries.TryGetValue(contentId, out var e))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        if (now - e.LastSeen < staleAfter)
        {
            return false;
        }

        return e.LastVerifyAttempt is not { } last || now - last >= retryCooldown;
    }

    /// <summary>Records that a stale re-verification was just attempted for the given Content ID
    /// (whether it succeeded or not), so the retry cooldown starts.</summary>
    public void MarkVerifyAttempt(ulong contentId)
    {
        if (entries.TryGetValue(contentId, out var e))
        {
            entries[contentId] = e with { LastVerifyAttempt = DateTime.UtcNow };
            dirty = true;
        }
    }

    /// <summary>
    /// Builds the previous-name list for a new write: the displaced current name (if changed) plus any
    /// seed history and the existing history, de-duplicated by name (keeping the most recent), newest
    /// first, capped. Returns <c>null</c> when empty.
    /// </summary>
    private static IReadOnlyList<PreviousName>? MergeHistory(
        CidCacheEntry? existing, string newName, IReadOnlyList<PreviousName>? seedHistory)
    {
        var candidates = new List<PreviousName>();

        if (existing != null && !string.IsNullOrEmpty(existing.Name)
            && !string.Equals(existing.Name, newName, StringComparison.Ordinal))
        {
            candidates.Add(new PreviousName(existing.Name, existing.WorldId, existing.WorldName, existing.LastSeen));
        }

        if (existing?.PreviousNames != null)
        {
            candidates.AddRange(existing.PreviousNames);
        }

        if (seedHistory != null)
        {
            candidates.AddRange(seedHistory);
        }

        var merged = candidates
            .Where(p => !string.IsNullOrEmpty(p.Name) && !string.Equals(p.Name, newName, StringComparison.Ordinal))
            .GroupBy(p => p.Name, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(p => p.SeenUntil).First())
            .OrderByDescending(p => p.SeenUntil)
            .Take(MaxPreviousNames)
            .ToList();

        return merged.Count == 0 ? null : merged;
    }

    /// <summary>
    /// Removes every cached entry and deletes the on-disk cache file. Backs the settings "clear cached
    /// names" action so the user can wipe stored name/world/history data on demand (the cache is otherwise
    /// kept indefinitely by design, since a Content ID's last-known name never expires).
    /// </summary>
    public void Clear()
    {
        entries.Clear();
        dirty = false;

        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            PassportCheckerReborn.Log.Information("[CidCache] Cleared all cached names.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[CidCache] Failed to delete CID cache file.");
        }
    }

    /// <summary>
    /// Flushes any pending changes to disk. No-op if the cache is not dirty.
    /// </summary>
    public void Save()
    {
        if (!dirty)
        {
            return;
        }

        try
        {
            // JSON doesn't allow integer keys, so we serialise the dictionary
            // with the Content ID rendered as a decimal string.
            var serialisable = new Dictionary<string, CidCacheEntry>(entries.Count);
            foreach (var (id, entry) in entries)
            {
                serialisable[id.ToString()] = entry;
            }

            var json = System.Text.Json.JsonSerializer.Serialize(serialisable, JsonOptions);
            File.WriteAllText(filePath, json);
            dirty = false;

            PassportCheckerReborn.Log.Debug($"[CidCache] Saved {entries.Count} entries to disk.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[CidCache] Failed to save CID cache.");
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => Save();

    // ── Private helpers ───────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var json = File.ReadAllText(filePath);
            var deserialised = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CidCacheEntry>>(json, JsonOptions);
            if (deserialised == null)
            {
                return;
            }

            foreach (var (key, entry) in deserialised)
            {
                if (ulong.TryParse(key, out var contentId) && contentId != 0)
                {
                    entries[contentId] = entry;
                }
            }

            PassportCheckerReborn.Log.Debug($"[CidCache] Loaded {entries.Count} entries from disk.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[CidCache] Failed to load CID cache.");
        }
    }
}
