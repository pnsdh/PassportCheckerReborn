using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PassportCheckerReborn.Services;

/// <summary>Where a known player was seen. A player can be known from several sources at once.</summary>
[Flags]
public enum KnownPlayerSource
{
    None = 0,
    Friend = 1 << 0,
    FreeCompany = 1 << 1,
    Linkshell = 1 << 2,
    CrossWorldLinkshell = 1 << 3,
}

/// <summary>
/// Remembers the local character's friends, free company members and linkshell / cross-world linkshell members,
/// by ContentId, so the member info overlay can highlight them (ContentId survives renames and world transfers,
/// and is known even for players whose adventure plate is hidden).
/// <para>
/// The game only loads these lists from the server when their window is opened, so this never asks for them:
/// it reads whatever the game currently holds every few seconds and keeps it on disk, per local character.
/// Friends and free company members are held whole by the game, so each read replaces the remembered list and
/// people who left drop out. The linkshell proxies only hold the shell being viewed, so those accumulate; the
/// settings page shows the counts and can clear them.
/// </para>
/// <para>
/// The game may keep a list loaded across a switch to another character. So when a different character logs in,
/// what the game still holds is set aside as "ignored" and only taken once it changes, i.e. once the window is
/// opened and that character's list is loaded.
/// </para>
/// </summary>
public sealed class KnownPlayerService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly PassportCheckerReborn plugin;
    private readonly string filePath;

    // localContentId → source → ContentIds. Touched on the framework thread only (polling and drawing).
    private readonly Dictionary<ulong, Dictionary<KnownPlayerSource, HashSet<ulong>>> lists = [];

    // Per source, the list contents to skip until they change (see the class remarks). Framework thread only.
    private readonly Dictionary<KnownPlayerSource, HashSet<ulong>> ignored = [];

    private DateTime nextPoll = DateTime.MinValue;
    private bool dirty;

    // The character the lists were last read for, to notice a switch to another one. Zero until the first read.
    private ulong lastPolledLocalId;

    public KnownPlayerService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        filePath = Path.Combine(PassportCheckerReborn.PluginInterface.GetPluginConfigDirectory(), "known_players.json");
        Load();
        PassportCheckerReborn.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        PassportCheckerReborn.Framework.Update -= OnFrameworkUpdate;
        Save();
    }

    /// <summary>The enabled sources <paramref name="contentId"/> is known from, for the logged-in character.</summary>
    public KnownPlayerSource GetSources(ulong contentId)
    {
        if (contentId == 0 || CurrentLists() is not { } current)
        {
            return KnownPlayerSource.None;
        }

        var result = KnownPlayerSource.None;
        foreach (var (source, ids) in current)
        {
            if (IsEnabled(source) && ids.Contains(contentId))
            {
                result |= source;
            }
        }

        return result;
    }

    /// <summary>How many players are remembered from <paramref name="source"/> for the logged-in character.</summary>
    public int Count(KnownPlayerSource source)
    {
        return CurrentLists() is { } current && current.TryGetValue(source, out var ids) ? ids.Count : 0;
    }

    /// <summary>Forgets everyone remembered from <paramref name="source"/> for the logged-in character.</summary>
    public void Clear(KnownPlayerSource source)
    {
        if (CurrentLists() is { } current && current.Remove(source))
        {
            dirty = true;
            Save();
        }

        // The game still holds the shell it last loaded, so that one shell's members come straight back: they
        // are its current members, while everyone accumulated from other shells is gone.
    }

    private bool IsEnabled(KnownPlayerSource source)
    {
        var cfg = plugin.Configuration;
        return source switch
        {
            KnownPlayerSource.Friend => cfg.HighlightFriends,
            KnownPlayerSource.FreeCompany => cfg.HighlightFreeCompanyMembers,
            KnownPlayerSource.Linkshell => cfg.HighlightLinkshellMembers,
            KnownPlayerSource.CrossWorldLinkshell => cfg.HighlightCrossWorldLinkshellMembers,
            _ => false,
        };
    }

    private Dictionary<KnownPlayerSource, HashSet<ulong>>? CurrentLists()
    {
        var localId = PassportCheckerReborn.PlayerState.ContentId;
        return localId != 0 && lists.TryGetValue(localId, out var current) ? current : null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll)
        {
            return;
        }

        nextPoll = now + PollInterval;

        if (!PassportCheckerReborn.PlayerState.IsLoaded || PassportCheckerReborn.PlayerState.ContentId == 0)
        {
            return;
        }

        try
        {
            Poll(PassportCheckerReborn.PlayerState.ContentId);
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[KnownPlayerService] Failed to read the social lists.");
            nextPoll = now + TimeSpan.FromMinutes(1);
        }

        // Written in the background of play rather than per change: these lists change rarely.
        Save();
    }

    private static readonly KnownPlayerSource[] Sources =
    [
        KnownPlayerSource.Friend,
        KnownPlayerSource.FreeCompany,
        KnownPlayerSource.Linkshell,
        KnownPlayerSource.CrossWorldLinkshell,
    ];

    private void Poll(ulong localId)
    {
        if (lastPolledLocalId != 0 && lastPolledLocalId != localId)
        {
            // Switched character: whatever is still loaded may belong to the previous one.
            foreach (var source in Sources)
            {
                ignored[source] = ReadIds(source);
            }
        }

        lastPolledLocalId = localId;

        if (!lists.TryGetValue(localId, out var current))
        {
            current = [];
            lists[localId] = current;
        }

        foreach (var source in Sources)
        {
            // Friends and free company members are held whole by the game; linkshells one shell at a time.
            Read(current, source, replace: source is KnownPlayerSource.Friend or KnownPlayerSource.FreeCompany);
        }
    }

    /// <summary>The ContentIds the game currently holds for one list (empty when it isn't loaded).</summary>
    private static unsafe HashSet<ulong> ReadIds(KnownPlayerSource source)
    {
        var proxy = source switch
        {
            KnownPlayerSource.Friend => (InfoProxyCommonList*)InfoProxyFriendList.Instance(),
            KnownPlayerSource.FreeCompany => (InfoProxyCommonList*)InfoProxyFreeCompanyMember.Instance(),
            KnownPlayerSource.Linkshell => (InfoProxyCommonList*)InfoProxyLinkshellMember.Instance(),
            KnownPlayerSource.CrossWorldLinkshell => (InfoProxyCommonList*)InfoProxyCrossWorldLinkshellMember.Instance(),
            _ => null,
        };

        var ids = new HashSet<ulong>();
        if (proxy == null)
        {
            return ids;
        }

        foreach (var entry in proxy->CharDataSpan)
        {
            if (entry.ContentId != 0)
            {
                ids.Add(entry.ContentId);
            }
        }

        return ids;
    }

    /// <summary>
    /// Takes the ContentIds the game currently holds for one list. An empty list means "not loaded", not
    /// "nobody", so it never clears what is remembered; a list set aside as ignored is skipped until it changes.
    /// </summary>
    private void Read(Dictionary<KnownPlayerSource, HashSet<ulong>> current, KnownPlayerSource source, bool replace)
    {
        var seen = ReadIds(source);
        if (seen.Count == 0)
        {
            return;
        }

        if (ignored.TryGetValue(source, out var skip))
        {
            if (seen.SetEquals(skip))
            {
                return;
            }

            ignored.Remove(source);
        }

        if (!current.TryGetValue(source, out var ids))
        {
            ids = [];
            current[source] = ids;
        }

        if (replace)
        {
            if (!ids.SetEquals(seen))
            {
                ids.Clear();
                ids.UnionWith(seen);
                dirty = true;
            }
        }
        else if (!seen.IsSubsetOf(ids))
        {
            ids.UnionWith(seen);
            dirty = true;
        }
    }

    private void Save()
    {
        if (!dirty)
        {
            return;
        }

        dirty = false;
        try
        {
            // Small (a few hundred ids at most), so written directly.
            var data = new Dictionary<string, Dictionary<KnownPlayerSource, List<ulong>>>();
            foreach (var (localId, bySource) in lists)
            {
                var sources = new Dictionary<KnownPlayerSource, List<ulong>>();
                foreach (var (source, ids) in bySource)
                {
                    sources[source] = [.. ids];
                }

                data[localId.ToString()] = sources;
            }

            File.WriteAllText(filePath, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[KnownPlayerService] Failed to save known players.");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return;
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<KnownPlayerSource, List<ulong>>>>(
                File.ReadAllText(filePath), JsonOptions);
            if (data == null)
            {
                return;
            }

            foreach (var (key, sources) in data)
            {
                if (!ulong.TryParse(key, out var localId))
                {
                    continue;
                }

                var bySource = new Dictionary<KnownPlayerSource, HashSet<ulong>>();
                foreach (var (source, ids) in sources)
                {
                    bySource[source] = [.. ids];
                }

                lists[localId] = bySource;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[KnownPlayerService] Failed to load known players.");
        }
    }
}
