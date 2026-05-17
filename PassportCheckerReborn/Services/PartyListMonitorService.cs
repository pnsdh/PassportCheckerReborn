using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using ECommons.DalamudServices;
using ECommons.GameHelpers;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using PassportCheckerReborn.Windows;
using System;
using System.Collections.Generic;

namespace PassportCheckerReborn.Services;

/// <summary>
/// Background service that continuously monitors the party list and caches player information
/// even when the Party List Window is not open.
/// </summary>
public sealed class PartyListMonitorService(PassportCheckerReborn plugin) : IDisposable
{
    private readonly PassportCheckerReborn plugin = plugin;
    private string lastPartyCompositionKey = string.Empty;
    private DateTime lastCheckTime = DateTime.MinValue;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Called by the Framework update hook to periodically check for party changes.
    /// </summary>
    public void OnFrameworkUpdate(IFramework framework)
    {
        try
        {
            if (!Player.Available)
            {
                return;
            }

            if (Svc.Condition?[ConditionFlag.InCombat] ?? false)
            {
                return;
            }

            // Only check every few seconds to avoid excessive processing
            var now = DateTime.UtcNow;
            if (now - lastCheckTime < CheckInterval)
            {
                return;
            }

            lastCheckTime = now;

            // Read current party members
            var partyMembers = ReadPartyMembers();
            if (partyMembers.Count == 0)
            {
                // Clear composition key when party is empty
                if (!string.IsNullOrEmpty(lastPartyCompositionKey))
                {
                    lastPartyCompositionKey = string.Empty;
                }
                return;
            }

            // Check if party composition changed
            var compositionKey = BuildCompositionKey(partyMembers);
            if (compositionKey != lastPartyCompositionKey)
            {
                lastPartyCompositionKey = compositionKey;
                PassportCheckerReborn.Log.Debug($"[PartyListMonitor] Party composition changed, caching {partyMembers.Count} members");

                // Cache all party members
                foreach (var member in partyMembers)
                {
                    if (member.ContentId != 0 && !string.IsNullOrEmpty(member.World))
                    {
                        plugin.CidCache.Set(member.ContentId, member.Name, member.WorldId, member.World);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyListMonitor] Error monitoring party list");
        }
    }

    /// <summary>
    /// Reads party members from the Dalamud IPartyList service, falling back to
    /// <see cref="InfoProxyCrossRealm"/> for crossworld parties when IPartyList is empty.
    /// </summary>
    private List<PartyMemberInfo> ReadPartyMembers()
    {
        var result = new List<PartyMemberInfo>();

        try
        {
            var partyList = PassportCheckerReborn.PartyList;
            if (partyList != null && partyList.Length > 0)
            {
                for (var i = 0; i < partyList.Length; i++)
                {
                    var member = partyList[i];
                    if (member == null)
                    {
                        continue;
                    }

                    var name = member.Name.TextValue;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var world = member.World.ValueNullable?.Name.ToString() ?? string.Empty;
                    var worldId = (ushort)member.World.RowId;
                    var classJob = member.ClassJob.ValueNullable;
                    var jobAbbreviation = classJob?.Abbreviation.ToString() ?? "???";
                    var contentId = member.ContentId;

                    result.Add(new PartyMemberInfo(name, world, jobAbbreviation, contentId, false, worldId));
                }

                return result;
            }

            // Fallback: read from InfoProxyCrossRealm for crossworld parties
            result = ReadCrossRealmPartyMembers();
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyListMonitor] Error reading party members");
        }

        return result;
    }

    /// <summary>
    /// Reads party members from <see cref="InfoProxyCrossRealm"/> when in a crossworld party.
    /// This provides member data (including ContentId) even when <see cref="Dalamud.Plugin.Services.IPartyList"/> hasn't populated.
    /// </summary>
    private unsafe List<PartyMemberInfo> ReadCrossRealmPartyMembers()
    {
        var result = new List<PartyMemberInfo>();
        try
        {
            var cwProxy = InfoProxyCrossRealm.Instance();
            if (cwProxy == null || !cwProxy->IsInCrossRealmParty)
            {
                return result;
            }

            var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
            var classJobSheet = PassportCheckerReborn.DataManager.GetExcelSheet<ClassJob>();

            var localIndex = cwProxy->LocalPlayerGroupIndex;
            var memberCount = InfoProxyCrossRealm.GetGroupMemberCount(localIndex);
            for (var i = 0; i < memberCount; i++)
            {
                var memberPtr = InfoProxyCrossRealm.GetGroupMember((uint)i, localIndex);
                if (memberPtr == null)
                {
                    continue;
                }

                var member = *memberPtr;
                if (member.HomeWorld == -1 || string.IsNullOrEmpty(member.NameString))
                {
                    continue;
                }

                var worldName = worldSheet?.GetRowOrDefault((uint)member.HomeWorld)?.Name.ToString()
                    ?? string.Empty;
                var jobAbbreviation = classJobSheet?.GetRowOrDefault(member.ClassJobId)?.Abbreviation.ToString()
                    ?? "???";
                var contentId = member.ContentId;

                result.Add(new PartyMemberInfo(member.NameString, worldName, jobAbbreviation, contentId, false, (ushort)member.HomeWorld));
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyListMonitor] Error reading cross-realm party members");
        }

        return result;
    }

    /// <summary>
    /// Builds a unique key representing the current party composition for detecting changes.
    /// </summary>
    private static string BuildCompositionKey(List<PartyMemberInfo> members)
    {
        var parts = new List<string>();
        foreach (var m in members)
        {
            parts.Add($"{m.ContentId}:{m.Name}@{m.World}:{m.JobAbbreviation}");
        }
        parts.Sort();
        return string.Join("|", parts);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
