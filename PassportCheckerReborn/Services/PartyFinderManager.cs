using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Gui.PartyFinder.Types;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Network;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace PassportCheckerReborn.Services;

/// <summary>
/// Manages hooks into the game's Party Finder UI and network packets
/// to discover the names of players in any selected PF listing.
///
/// <para>
/// Features implemented:
/// <list type="bullet">
///   <item>IAddonLifecycle hooks for LookingForGroupDetail open/refresh/close.</item>
///   <item>IAddonLifecycle hooks for LookingForGroup (list addon) setup/finalize.</item>
///   <item>IPartyFinderGui.ReceiveListing subscription to cache PF listing host data.</item>
///   <item>AgentLookingForGroup.PopulateListingData hook to extract member content IDs.</item>
///   <item>Auto-refresh timer (configurable interval, pauses when detail pane is open).</item>
///   <item>Context-menu "View Recruitment" injection.</item>
///   <item>High-end duty detection via ContentFinderCondition sheet.</item>
/// </list>
/// </para>
/// </summary>
public sealed class PartyFinderManager : IDisposable
{
    private readonly PassportCheckerReborn plugin;

    /// <summary>Players visible in the currently selected PF listing.</summary>
    /// <summary>An immutable snapshot of the current members — safe to enumerate on the UI thread.</summary>
    public IReadOnlyList<Windows.PartyMemberInfo> CurrentMembers => MembersSnapshot();

    private readonly List<Windows.PartyMemberInfo> currentMembers = [];

    // currentMembers is read on the framework/UI thread(s) and structurally mutated (Clear/Add/RemoveAt) plus
    // element-replaced from the background resolve task, so all access goes through membersLock. The helpers
    // below are the only touch points, and none holds the lock across an await.
    private readonly object membersLock = new();

    private Windows.PartyMemberInfo[] MembersSnapshot()
    {
        lock (membersLock)
        {
            return currentMembers.ToArray();
        }
    }

    private int MembersCount()
    {
        lock (membersLock)
        {
            return currentMembers.Count;
        }
    }

    private bool TryGetMember(int index, out Windows.PartyMemberInfo member)
    {
        lock (membersLock)
        {
            if (index >= 0 && index < currentMembers.Count)
            {
                member = currentMembers[index];
                return true;
            }

            member = default!;
            return false;
        }
    }

    /// <summary>Replaces slot [index] only if it still holds <paramref name="expectedContentId"/> (re-checked under lock).</summary>
    private bool ReplaceMemberIfMatch(int index, ulong expectedContentId, Windows.PartyMemberInfo replacement)
    {
        lock (membersLock)
        {
            if (index >= 0 && index < currentMembers.Count && currentMembers[index].ContentId == expectedContentId)
            {
                currentMembers[index] = replacement;
                return true;
            }

            return false;
        }
    }

    private bool RemoveMemberIfMatch(int index, ulong expectedContentId)
    {
        lock (membersLock)
        {
            if (index >= 0 && index < currentMembers.Count && currentMembers[index].ContentId == expectedContentId)
            {
                currentMembers.RemoveAt(index);
                return true;
            }

            return false;
        }
    }

    private void AddMember(Windows.PartyMemberInfo member)
    {
        lock (membersLock)
        {
            currentMembers.Add(member);
        }
    }

    private void ClearMembers()
    {
        lock (membersLock)
        {
            currentMembers.Clear();
        }
    }

    /// <summary>Whether the PF detail addon is currently open.</summary>
    public bool IsDetailOpen { get; private set; }

    /// <summary>Whether the PF list addon is currently open.</summary>
    public bool IsListOpen { get; private set; }

    /// <summary>The detected duty ID from the current PF listing (0 if unknown).</summary>
    public uint CurrentDutyId { get; private set; }

    /// <summary>Whether the current listing is for a high-end duty.</summary>
    public bool IsHighEndDuty { get; private set; }

    /// <summary>The detected duty name from the current PF listing (empty if unknown).</summary>
    public string CurrentDutyName { get; private set; } = string.Empty;

    /// <summary>
    /// Monotonically increasing counter that increments each time a new
    /// LookingForGroupDetail pane is opened. Used by the overlay to detect
    /// when cached data should be cleared.
    /// </summary>
    public int DetailOpenGeneration { get; private set; }

    /// <summary>
    /// Whether any current members have unresolved names (still being looked up
    /// via adventure plate / CharaCard). The overlay uses this to disable buttons
    /// that require resolved names.
    /// </summary>
    public bool HasUnresolvedMembers
    {
        get
        {
            foreach (var m in MembersSnapshot())
            {
                if (m.Name.StartsWith(UnresolvedNamePrefix))
                {
                    return true;
                }
            }

            return false;
        }
    }

    // ── Prevent auto-close on party changes ──────────────────────────────────
    private int trackedPartyMemberCount;

    // ── Auto-refresh ─────────────────────────────────────────────────────────
    private System.Timers.Timer? autoRefreshTimer;
    private int autoRefreshCountdown;

    // ── Known-player cache ───────────────────────────────────────────────────
    // ── Blacklist cache ───────────────────────────────────────────────────────
    /// <summary>
    /// Set of "Name@World" strings read from the game's BlackList addon (node #9).
    /// Refreshed whenever the BlackList addon is opened/refreshed, and also when
    /// a PF detail pane is opened.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> blacklistedPlayers = new(StringComparer.OrdinalIgnoreCase);

    // ── PF Listing cache (from IPartyFinderGui.ReceiveListing) ──────────────
    /// <summary>
    /// Cache of player info collected from PF listing packets.
    /// Maps ContentId → (Name, HomeWorldId, ListingId) for PF listing hosts.
    /// Populated via <see cref="IPartyFinderGui.ReceiveListing"/> events,
    /// following the same pattern as OpenRadar's Network.ListingHostExtract.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, (string Name, ushort WorldId, ulong ListingId)> pfListingPlayerCache = new();

    // ── PopulateListingData hook (extracts member content IDs from PF detail) ─
    /// <summary>
    /// Hook on <see cref="AgentLookingForGroup.PopulateListingData"/> to intercept
    /// the detailed listing data when a user clicks on a PF listing.
    /// This provides <c>MemberContentIds</c> and <c>Jobs</c> arrays for all party
    /// slots, following the same pattern as OpenRadar's PopulateListingDataDetour.
    /// </summary>
    private Hook<AgentLookingForGroup.Delegates.PopulateListingData>? populateListingHook;

    /// <summary>
    /// The most recently intercepted detailed listing data from PopulateListingData.
    /// Contains MemberContentIds and Jobs for all party members in the current listing.
    /// </summary>
    private AgentLookingForGroup.Detailed? currentDetailedPost;

    // ── CharaCard (adventure plate) name resolution ─────────────────────────
    /// <summary>
    /// Hook on <see cref="CharaCard.HandleCurrentCharaCardDataPacket"/> to intercept
    /// adventure plate responses that contain a player's name and world.
    /// Follows the same pattern as OpenRadar's CharaCardPacketHandlerDetour.
    /// </summary>
    private Hook<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>? charaCardPacketHandlerHook;

    /// <summary>
    /// Hook on <see cref="RaptureLogModule.ShowLogMessage"/> to suppress error messages
    /// (logMessageIds 5854-5861) generated when a CharaCard request fails (e.g. player
    /// has adventure plate disabled). Also signals a failed lookup so the awaiter can
    /// move on.
    /// </summary>
    private Hook<RaptureLogModule.Delegates.ShowLogMessage>? showLogMessageHook;

    /// <summary>
    /// Fired when an adventure plate response is received.
    /// <c>null</c> = timeout / unknown failure (remove member).
    /// Non-null with <c>IsAdventurePlateHidden = true</c> = ShowLogMessage suppressed (keep as Private Player).
    /// Non-null with a valid name = successfully resolved.
    /// </summary>
    private event Action<CharaCardResult?>? OnCharaCardReceived;

    private readonly record struct CharaCardResult(
        ulong ContentId,
        string Name,
        ushort WorldId,
        bool IsAdventurePlateHidden);

    /// <summary>Serialises CharaCard requests so only one is in-flight at a time.</summary>
    private readonly SemaphoreSlim charaCardRequestGate = new(1, 1);

    /// <summary>Cancellation source for the ongoing async name resolution batch.</summary>
    private CancellationTokenSource? resolveCts;

    /// <summary>
    /// Tracks whether a CharaCard name resolution request is currently in-flight.
    /// Used by <see cref="ShowLogMessageDetour"/> to only suppress error messages
    /// during active player name resolution, not at all times.
    /// </summary>
    private volatile bool isResolvingPlayerNames;

    /// <summary>Throttle interval between CharaCard requests (milliseconds).</summary>
    private const int CharaCardThrottleMs = 900;

    /// <summary>A cached name older than this is considered stale and eligible for re-verification (configurable).</summary>
    private TimeSpan StaleNameThreshold => TimeSpan.FromDays(Math.Max(1, plugin.Configuration.StaleNameThresholdDays));

    /// <summary>Minimum time between re-verification attempts for the same stale name, success or failure (configurable).</summary>
    private TimeSpan ReverifyCooldown => TimeSpan.FromHours(Math.Max(1, plugin.Configuration.ReverifyCooldownHours));

    /// <summary>
    /// ContentId → last time a CharaCard lookup found the plate hidden. Throttles doomed re-attempts
    /// for private players. Session-scoped (resets on reload, so a restart re-checks once); the cooldown
    /// duration is configurable via <see cref="Configuration.PrivatePlayerReverifyCooldownHours"/>.
    /// </summary>
    private readonly ConcurrentDictionary<ulong, DateTime> lastHiddenPlateAttempt = new();

    /// <summary>Whether a CharaCard lookup for this player was recently found hidden (within the cooldown).</summary>
    private bool IsRecentlyHiddenPlate(ulong contentId)
    {
        if (contentId == 0)
        {
            return false;
        }

        var cooldown = TimeSpan.FromHours(Math.Max(1, plugin.Configuration.PrivatePlayerReverifyCooldownHours));
        return lastHiddenPlateAttempt.TryGetValue(contentId, out var last)
            && DateTime.UtcNow - last < cooldown;
    }

    /// <summary>Prefix used for members whose names could not be resolved from cache.</summary>
    public const string UnresolvedNamePrefix = "Player ";

    /// <summary>Prefix used for members whose names failed to resolve via adventure plate.</summary>
    public const string UnresolvedPlayerPrefix = "Unresolved Player ";

    // ── Context-menu "View Recruitment" pending selection ─────────────────
    /// <summary>
    /// When non-zero, the content ID of a player whose PF listing should be
    /// auto-selected once the LookingForGroup addon is ready.
    /// </summary>
    private ulong pendingRecruitmentContentId;

    // ── Context-menu entry ───────────────────────────────────────────────────
    public PartyFinderManager(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        RegisterHooks();

        // Seed the in-memory dict from the persisted JSON cache so the blacklist
        // is ready immediately, before the BlackList addon has been opened.
        SeedBlacklistFromCache();

        // Eagerly read the live string array – it's always in memory even without
        // the BlackList addon being open, so this should succeed straight away and
        // will overwrite the cache seed with the current game state.
        ReadBlacklistFromAddon();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Hook Registration
    // ═════════════════════════════════════════════════════════════════════════

    private unsafe void RegisterHooks()
    {
        // ── LookingForGroupDetail (PF Detail pane) ──────────────────────────
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup, "LookingForGroupDetail", OnPFDetailSetup);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostRefresh, "LookingForGroupDetail", OnPFDetailRefresh);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize, "LookingForGroupDetail", OnPFDetailFinalize);

        // ── LookingForGroup (PF List) ───────────────────────────────────────
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup, "LookingForGroup", OnPFListSetup);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostRefresh, "LookingForGroup", OnPFListRefresh);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize, "LookingForGroup", OnPFListFinalize);

        // ── BlackList addon ──────────────────────────────────────────────────
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup, "BlackList", OnBlacklistAddonUpdated);
        PassportCheckerReborn.AddonLifecycle.RegisterListener(
            AddonEvent.PostRefresh, "BlackList", OnBlacklistAddonUpdated);

        // ── IPartyFinderGui listing subscription ────────────────────────────
        // Captures host ContentId/Name/World from every PF listing packet.
        PassportCheckerReborn.PartyFinderGui.ReceiveListing += OnReceiveListing;

        // ── AgentLookingForGroup.PopulateListingData hook ───────────────────
        // Intercepts the detailed listing data to extract MemberContentIds + Jobs.
        try
        {
            populateListingHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<AgentLookingForGroup.Delegates.PopulateListingData>(
                AgentLookingForGroup.Addresses.PopulateListingData.Value,
                PopulateListingDataDetour);
            populateListingHook.Enable();
        }
        catch (Exception)
        {
            //PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook PopulateListingData.");
        }

        // ── CharaCard (adventure plate) hooks ───────────────────────────────
        // Intercepts adventure plate responses to resolve player names from CIDs.
        // Follows the same pattern as OpenRadar's CharaCard hooks.
        try
        {
            charaCardPacketHandlerHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<CharaCard.Delegates.HandleCurrentCharaCardDataPacket>(
                CharaCard.Addresses.HandleCurrentCharaCardDataPacket.Value,
                CharaCardPacketHandlerDetour);
            charaCardPacketHandlerHook.Enable();

            showLogMessageHook = PassportCheckerReborn.GameInteropProvider.HookFromAddress<RaptureLogModule.Delegates.ShowLogMessage>(
                RaptureLogModule.Addresses.ShowLogMessage.Value,
                ShowLogMessageDetour);
            showLogMessageHook.Enable();
        }
        catch (Exception)
        {
            //PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to hook CharaCard/ShowLogMessage.");
        }

        // ── Track party member count (initial snapshot for close-suppression) ──
        trackedPartyMemberCount = GetEffectivePartyCount();
        //PassportCheckerReborn.Log.Information(
        //    $"[PartyFinderManager] Initial tracked party count: {trackedPartyMemberCount} " +
        //    $"(IPartyList={PassportCheckerReborn.PartyList.Length})");

        // ── Context menu (right-click → "View Recruitment") ─────────────────
        if (plugin.Configuration.RightClickPlayerNameForRecruitment3)
        {
            RegisterContextMenu();
        }
    }

    /// <summary>
    /// Returns the current party member count using the best available source.
    /// <see cref="IPartyList.Length"/> returns 0 for cross-world parties (which PF
    /// always creates), so we read from <see cref="InfoProxyCrossRealm"/> first.
    /// </summary>
    public static unsafe int GetEffectivePartyCount()
    {
        try
        {
            var cwProxy = InfoProxyCrossRealm.Instance();
            if (cwProxy != null && cwProxy->IsInCrossRealmParty)
            {
                var localIndex = cwProxy->LocalPlayerGroupIndex;
                return InfoProxyCrossRealm.GetGroupMemberCount(localIndex);
            }
        }
        catch
        {
            // Fall through to IPartyList
        }

        return PassportCheckerReborn.PartyList.Length;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // IPartyFinderGui Listing Handler
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Called for each PF listing received from the game server.
    /// Caches the listing host's ContentId, Name, and World so they can be
    /// used when the user opens a PF detail pane.
    /// </summary>
    private void OnReceiveListing(IPartyFinderListing listing, IPartyFinderListingEventArgs args)
    {
        if (listing.ContentId == 0)
        {
            return;
        }

        var name = listing.Name.TextValue;
        var worldId = (ushort)listing.HomeWorld.RowId;
        var listingId = listing.Id;
        pfListingPlayerCache[listing.ContentId] = (name, worldId, listingId);

        // Persist to disk so the name is available in future sessions without
        // needing another CharaCard request. Always overwrite: PF packet data is
        // the freshest available source and handles name/world changes.
        var worldName = PassportCheckerReborn.DataManager.GetExcelSheet<World>()
            ?.GetRowOrDefault(worldId)?.Name.ToString() ?? string.Empty;
        plugin.CidCache.Set(listing.ContentId, name, worldId, worldName);
    }

    // ═════════════════════════════════════════════════════════════════════════
    // PopulateListingData Hook (extracts member content IDs)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detour for <see cref="AgentLookingForGroup.PopulateListingData"/>.
    /// Intercepts the <see cref="AgentLookingForGroup.Detailed"/> struct that the game
    /// populates when the user clicks on a PF listing. This struct contains
    /// <c>MemberContentIds</c> and <c>Jobs</c> arrays for all 8 party slots.
    /// </summary>
    private unsafe void PopulateListingDataDetour(AgentLookingForGroup* thisPtr, AgentLookingForGroup.Detailed* listingData)
    {
        try
        {
            // Guard the native pointer — a null deref here is an uncatchable AV that would crash the game.
            if (listingData != null)
            {
                currentDetailedPost = *listingData;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] PopulateListingData detour error.");
        }

        populateListingHook!.Original(thisPtr, listingData);
    }

    /// <summary>
    /// Attempts to resolve a content ID to a player name and world using the PF listing cache.
    /// </summary>
    public (string Name, string World, NameSource Source)? ResolvePlayerFromCache(ulong contentId)
    {
        if (contentId == 0)
        {
            return null;
        }

        // Prefer the in-memory PF listing cache (always fresh from network packets).
        if (pfListingPlayerCache.TryGetValue(contentId, out var cached))
        {
            var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
            var worldName = worldSheet?.GetRowOrDefault(cached.WorldId)?.Name.ToString() ?? string.Empty;
            return (cached.Name, worldName, NameSource.Live);
        }

        // Fall back to the persistent CID cache for CIDs seen in previous sessions.
        // This avoids a CharaCard round-trip for already-known players.
        // Note: the entry may be stale if the player changed their name or world;
        // it will be overwritten the next time fresh data arrives.
        if (plugin.CidCache.TryGet(contentId, out var persisted) && persisted != null
            && !string.IsNullOrEmpty(persisted.Name))
        {
            return (persisted.Name, persisted.WorldName, persisted.Source);
        }

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // CharaCard (Adventure Plate) Hooks & Resolution
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Detour for <see cref="CharaCard.HandleCurrentCharaCardDataPacket"/>.
    /// Fires when the game receives an adventure plate response for a requested CID.
    /// Extracts ContentId, Name, and WorldId from the packet.
    /// </summary>
    private unsafe void CharaCardPacketHandlerDetour(CharaCard* thisPtr, CharaCardPacket* packet)
    {
        // Guard the native pointer before dereferencing — a null packet would be an uncatchable access
        // violation the try/catch can't save, so fall straight through to the original in that case.
        if (packet != null)
        {
            try
            {
                var nameSpan = packet->Name;
                var name = nameSpan.Length > 4
                    ? System.Text.Encoding.UTF8.GetString(nameSpan[4..]).TrimEnd('\0')
                    : string.Empty;

                OnCharaCardReceived?.Invoke(new CharaCardResult(packet->ContentId, name, packet->WorldId, false));
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex, "[PCR:CharaCard] Detour error.");
            }
        }

        charaCardPacketHandlerHook!.Original(thisPtr, packet);
    }

    /// <summary>
    /// Detour for <see cref="RaptureLogModule.ShowLogMessage"/>.
    /// Suppresses error log messages (IDs 5855-5860) generated when a CharaCard
    /// request fails (e.g. player has adventure plate disabled), and signals a
    /// failed lookup via the <see cref="OnCharaCardReceived"/> event.
    /// Uses the same exclusive range (> 5854 and &lt; 5861) as OpenRadar.
    /// Only suppresses during active player name resolution (when <see cref="isResolvingPlayerNames"/> is true).
    /// </summary>
    private unsafe void ShowLogMessageDetour(RaptureLogModule* thisPtr, uint logMessageId)
    {
        // Only suppress error messages during active player name resolution
        if (isResolvingPlayerNames && logMessageId is > 5854 and < 5861)
        {
            PassportCheckerReborn.Log.Debug(
                $"[PCR:CharaCard] ShowLogMessage suppressed (id={logMessageId}) — adventure plate hidden/unavailable, signalling private.");
            // Signal with IsAdventurePlateHidden=true so the awaiter can distinguish
            // a deliberately hidden plate from a generic timeout/failure.
            OnCharaCardReceived?.Invoke(new CharaCardResult(0, string.Empty, 0, IsAdventurePlateHidden: true));
            return;
        }

        showLogMessageHook!.Original(thisPtr, logMessageId);
    }

    /// <summary>
    /// Sends a CharaCard (adventure plate) request for the given content ID and waits for
    /// the response. Returns a <see cref="CharaCardResult"/> on any definitive answer, or
    /// <c>null</c> on generic timeout / failure (caller should treat as unresolvable and remove).
    /// Throttled to one request per <see cref="CharaCardThrottleMs"/> milliseconds, serialised via semaphore.
    /// </summary>
    private async Task<CharaCardResult?> RequestCharaCardAsync(ulong contentId, CancellationToken ct)
    {
        await charaCardRequestGate.WaitAsync(ct);

        try
        {
            // Set flag to enable error message suppression during this request
            isResolvingPlayerNames = true;

            // Throttle between requests
            await Task.Delay(CharaCardThrottleMs, ct);

            var tcs = new TaskCompletionSource<CharaCardResult?>();

            void Handler(CharaCardResult? result)
            {
                // IsAdventurePlateHidden signals are not tied to a specific CID (the game
                // doesn't echo the CID back), but because requests are serialised through
                // the gate exactly one request is in-flight at any given time, so any
                // hidden-plate signal must belong to the current CID.
                if (result is { IsAdventurePlateHidden: true })
                {
                    tcs.TrySetResult(new CharaCardResult(contentId, string.Empty, 0, IsAdventurePlateHidden: true));
                }
                else if (result is { } r && r.ContentId == contentId)
                {
                    tcs.TrySetResult(r);
                }
            }

            OnCharaCardReceived += Handler;

            await PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
            {
                unsafe
                {
                    // Guard against a null instance (game not fully ready): skip the request and let the
                    // 5s timeout below resolve this lookup as a failure rather than risk a null-deref crash.
                    var instance = CharaCard.Instance();
                    if (instance != null)
                    {
                        instance->RequestCharaCardForContentId(contentId);
                    }
                }
            });

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

                var result = await tcs.Task.WaitAsync(timeoutCts.Token);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timed out — generic failure, caller removes the member
                return null;
            }
            finally
            {
                OnCharaCardReceived -= Handler;
            }
        }
        finally
        {
            // Always clear the flag when the request completes or fails
            isResolvingPlayerNames = false;
            charaCardRequestGate.Release();
        }
    }

    /// <summary>
    /// Asynchronously resolves names for members that could not be resolved from
    /// the PF listing cache. Depending on <see cref="Configuration.PlayerTrackPriority"/>,
    /// consults the PlayerTrack database and the CharaCard (adventure plate) lookup in the
    /// configured order. Updates the member list in-place when a name is resolved.
    /// </summary>
    private async Task ResolveUnresolvedMembersAsync(CancellationToken ct)
    {
        var cfg = plugin.Configuration;
        var ptEnabled = cfg.EnablePlayerTrackIntegration && plugin.PlayerTrackService?.IsUsable == true;
        var ptFirst = ptEnabled && cfg.PlayerTrackPriority == PlayerTrackResolutionPriority.PlayerTrackFirst;
        var ptFallback = ptEnabled && cfg.PlayerTrackPriority == PlayerTrackResolutionPriority.CharaCardFirst;
        var charaCardAvailable = charaCardPacketHandlerHook is { IsEnabled: true };

        var resolved = false;

        // ── Phase 0: PlayerTrack-first pass (or the only source when CharaCard is unavailable) ──
        // Runs off the main thread since it performs blocking SQLite reads.
        if (ptFirst || (ptEnabled && !charaCardAvailable))
        {
            try
            {
                await Task.Run(() =>
                {
                    for (var i = 0; i < MembersCount(); i++)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!TryGetMember(i, out var m))
                        {
                            break; // list shrank concurrently (detail pane closed) — stop.
                        }

                        if (m.ContentId == 0 || !m.Name.StartsWith(UnresolvedNamePrefix))
                        {
                            continue;
                        }

                        TryApplyPlayerTrack(i, m);
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        // ── Phase 1: CharaCard for still-unresolved members ──
        if (charaCardAvailable)
        {
            for (var i = 0; i < MembersCount(); i++)
            {
                if (ct.IsCancellationRequested)
                {
                    PassportCheckerReborn.Log.Debug("[PCR:Resolve] Batch cancelled.");
                    return;
                }

                if (!TryGetMember(i, out var member))
                {
                    break;
                }

                if (member.ContentId == 0 || !member.Name.StartsWith(UnresolvedNamePrefix))
                {
                    continue;
                }

                try
                {
                    // Throttle: if this player's plate was recently found hidden, skip the doomed
                    // CharaCard request (still try the PlayerTrack fallback below, then mark Private).
                    var recentlyHidden = IsRecentlyHiddenPlate(member.ContentId);

                    CharaCardResult? info = null;
                    if (!recentlyHidden)
                    {
                        PassportCheckerReborn.Log.Debug($"[PCR:Resolve] Attempting CharaCard lookup for slot {i}");
                        info = await RequestCharaCardAsync(member.ContentId, ct);
                        if (ct.IsCancellationRequested)
                        {
                            PassportCheckerReborn.Log.Debug("[PCR:Resolve] Cancelled after CharaCard request.");
                            return;
                        }
                    }

                    if (info is { } resolvedInfo && !string.IsNullOrEmpty(resolvedInfo.Name))
                    {
                        // CharaCard succeeded — freshest possible data; clear the hidden throttle and cache it.
                        lastHiddenPlateAttempt.TryRemove(member.ContentId, out _);

                        var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
                        var worldName = worldSheet?.GetRowOrDefault(resolvedInfo.WorldId)?.Name.ToString() ?? string.Empty;

                        var existingListingId = pfListingPlayerCache.TryGetValue(member.ContentId, out var prev) ? prev.ListingId : 0ul;
                        pfListingPlayerCache[member.ContentId] = (resolvedInfo.Name, resolvedInfo.WorldId, existingListingId);
                        plugin.CidCache.Set(member.ContentId, resolvedInfo.Name, resolvedInfo.WorldId, worldName);
                        resolved = true;

                        ReplaceMemberIfMatch(i, member.ContentId, member with { Name = resolvedInfo.Name, World = worldName });
                    }
                    else if (ptFallback && TryApplyPlayerTrack(i, member))
                    {
                        // CharaCard couldn't resolve (hidden / empty / timeout) but PlayerTrack had a record.
                    }
                    else if (recentlyHidden || info is { IsAdventurePlateHidden: true })
                    {
                        // Adventure plate hidden — either freshly detected or still within the throttle window.
                        // Record the attempt time only on a fresh detection so the cooldown can actually elapse.
                        if (!recentlyHidden)
                        {
                            lastHiddenPlateAttempt[member.ContentId] = DateTime.UtcNow;
                        }

                        var slotNumber = i + 1;
                        ReplaceMemberIfMatch(i, member.ContentId, member with { Name = $"Private Player {slotNumber}", IsPrivate = true });
                    }
                    else if (info is { ContentId: not 0 })
                    {
                        // The adventure plate response came back with a matching CID but an empty name.
                        // This can happen for cross-DC players or certain account types whose plate
                        // does not include a name in the packet. Keep the slot as Unresolved Player so
                        // the PF row stays intact rather than silently removing a valid member.
                        var slotNumber = i + 1;
                        PassportCheckerReborn.Log.Information(
                            $"[PCR:Resolve] Adventure plate returned empty name for slot {i}) — marking as Unresolved Player {slotNumber}.");
                        ReplaceMemberIfMatch(i, member.ContentId, member with { Name = $"Unresolved Player {slotNumber}" });
                    }
                    else
                    {
                        // Generic timeout or unrecognised failure — unable to resolve, remove the slot.
                        PassportCheckerReborn.Log.Information(
                            $"[PCR:Resolve] Unable to resolve player info for slot {i} — removing.");
                        if (RemoveMemberIfMatch(i, member.ContentId))
                        {
                            i--;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    PassportCheckerReborn.Log.Debug("[PCR:Resolve] OperationCanceledException — stopping batch.");
                    return;
                }
                catch (Exception ex)
                {
                    PassportCheckerReborn.Log.Warning(ex,
                        $"[PCR:Resolve] Exception resolving.");
                }
            }
        }

        // ── Phase 2: re-verify stale cached names via CharaCard (throttled) ──
        if (charaCardAvailable && cfg.EnableStaleNameReverification)
        {
            for (var i = 0; i < MembersCount(); i++)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (!TryGetMember(i, out var member))
                {
                    break;
                }

                if (member.ContentId == 0 || member.IsPrivate
                    || member.Name.StartsWith(UnresolvedNamePrefix) || member.Name.StartsWith(UnresolvedPlayerPrefix))
                {
                    continue;
                }

                if (!plugin.CidCache.ShouldReverify(member.ContentId, StaleNameThreshold, ReverifyCooldown))
                {
                    continue;
                }

                // Record the attempt up front so we back off even if the plate is hidden / times out.
                plugin.CidCache.MarkVerifyAttempt(member.ContentId);

                try
                {
                    var info = await RequestCharaCardAsync(member.ContentId, ct);
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (info is { } r && !string.IsNullOrEmpty(r.Name))
                    {
                        var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();
                        var worldName = worldSheet?.GetRowOrDefault(r.WorldId)?.Name.ToString() ?? string.Empty;

                        var existingListingId = pfListingPlayerCache.TryGetValue(member.ContentId, out var prev) ? prev.ListingId : 0ul;
                        pfListingPlayerCache[member.ContentId] = (r.Name, r.WorldId, existingListingId);
                        // Set() refreshes LastSeen (clearing staleness) and records any rename into history.
                        plugin.CidCache.Set(member.ContentId, r.Name, r.WorldId, worldName);
                        resolved = true;

                        var nameChanged = !string.Equals(r.Name, member.Name, StringComparison.Ordinal);
                        if ((nameChanged || member.FromPlayerTrack)
                            && ReplaceMemberIfMatch(i, member.ContentId,
                                member with { Name = r.Name, World = worldName, FromPlayerTrack = false })
                            && nameChanged)
                        {
                            PassportCheckerReborn.Log.Debug($"[PCR:Reverify] Slot {i} name refreshed to a newer value.");
                        }
                    }

                    // Failure (hidden plate / timeout): keep the existing name; the back-off is already recorded.
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    PassportCheckerReborn.Log.Warning(ex, "[PCR:Reverify] Error re-verifying a stale name.");
                }
            }
        }

        PassportCheckerReborn.Log.Debug(
            $"[PCR:Resolve] Batch complete — final member count={MembersCount()}.");

        if (resolved)
        {
            plugin.CidCache.Save();
        }
    }

    /// <summary>
    /// Returns <c>true</c> if any currently-resolved member has a cached name old enough to be
    /// re-verified (and not within the retry cooldown). Cheap — reads only the in-memory cache.
    /// </summary>
    private bool AnyMemberNeedsReverify()
    {
        foreach (var m in MembersSnapshot())
        {
            if (m.ContentId == 0 || m.IsPrivate
                || m.Name.StartsWith(UnresolvedNamePrefix) || m.Name.StartsWith(UnresolvedPlayerPrefix))
            {
                continue;
            }

            if (plugin.CidCache.ShouldReverify(m.ContentId, StaleNameThreshold, ReverifyCooldown))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to resolve <paramref name="member"/> (identified by ContentId) from the PlayerTrack
    /// database and, if found, updates the slot in place with the name/world, marks it PlayerTrack-sourced,
    /// and write-through caches the name (detail is retained in <c>PlayerTrackService</c>). Returns
    /// <c>true</c> when a name was applied. Never throws.
    /// </summary>
    private bool TryApplyPlayerTrack(int index, Windows.PartyMemberInfo member)
    {
        try
        {
            var svc = plugin.PlayerTrackService;
            if (svc is null || !plugin.Configuration.EnablePlayerTrackIntegration || !svc.IsUsable)
            {
                return false;
            }

            var rec = svc.Resolve(member.ContentId);
            if (rec is null || string.IsNullOrEmpty(rec.Name))
            {
                return false;
            }

            // Persist the name into the CID cache (write-through, marked PlayerTrack-sourced) so future
            // PF opens resolve instantly from cache instead of re-querying PlayerTrack / re-attempting
            // doomed CharaCard lookups. Live data (PF packet / adventure plate) will overwrite it.
            // PlayerTrack's own name history seeds the entry's previous-names list.
            var seedHistory = new List<PreviousName>(rec.PreviousNames.Count);
            foreach (var h in rec.PreviousNames)
            {
                seedHistory.Add(new PreviousName(h.Name, h.WorldId, h.WorldName, h.Changed));
            }

            plugin.CidCache.SetFromPlayerTrack(
                member.ContentId, rec.Name, rec.WorldId, rec.WorldName, rec.LastSeen, seedHistory);

            if (ReplaceMemberIfMatch(index, member.ContentId, member with
                {
                    Name = rec.Name,
                    World = rec.WorldName,
                    IsPrivate = false,
                    FromPlayerTrack = true,
                }))
            {
                PassportCheckerReborn.Log.Debug(
                    $"[PCR:Resolve] Slot {index} resolved via PlayerTrack (last seen {rec.LastSeen:u}).");
                return true;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PCR:Resolve] PlayerTrack lookup failed.");
        }

        return false;
    }

    private void OnPFDetailSetup(AddonEvent type, AddonArgs args)
    {
        IsDetailOpen = true;
        DetailOpenGeneration++;
        trackedPartyMemberCount = GetEffectivePartyCount();
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] Detail OPENED (gen={DetailOpenGeneration}, partyCount={trackedPartyMemberCount}).");

        // Pause auto-refresh while the detail pane is open (matches DailyRoutines pattern)
        StopAutoRefreshTimer();

        // Refresh the blacklist cache in case entries changed since last open
        ReadBlacklistFromAddon();

        // Read duty name from the addon, then detect duty and populate members
        ReadDutyNameFromAddon();
        DetectCurrentDuty();
        RefreshMembers();

        // Auto-show overlay when config enables it
        if (plugin.Configuration.ShowMemberInfoOverlay)
        {
            plugin.PFWindow.IsOpen = true;
        }
    }

    private void OnPFDetailRefresh(AddonEvent type, AddonArgs args)
    {
        ReadDutyNameFromAddon();
        DetectCurrentDuty();
        RefreshMembers();
    }

    private void OnPFDetailFinalize(AddonEvent type, AddonArgs args)
    {
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] Detail CLOSED (partyCount={GetEffectivePartyCount()}, tracked={trackedPartyMemberCount}).");
        // Cancel any in-progress CharaCard resolution
        resolveCts?.Cancel();
        resolveCts?.Dispose();
        resolveCts = null;

        IsDetailOpen = false;
        plugin.PFWindow.IsOpen = false;
        ClearMembers();
        currentDetailedPost = null;
        CurrentDutyId = 0;
        CurrentDutyName = string.Empty;
        IsHighEndDuty = false;

        // Persist any names that were cached during this detail-pane session.
        plugin.CidCache.Save();

        // Resume auto-refresh
        if (IsListOpen)
        {
            StartAutoRefreshTimer();
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // PF List Handlers
    // ═════════════════════════════════════════════════════════════════════════

    private void OnPFListSetup(AddonEvent type, AddonArgs args)
    {
        IsListOpen = true;
        trackedPartyMemberCount = GetEffectivePartyCount();
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] List OPENED (partyCount={trackedPartyMemberCount}).");
        StartAutoRefreshTimer();
        TryProcessPendingRecruitment();
    }

    private void OnPFListRefresh(AddonEvent type, AddonArgs args)
    {
        // Reset auto-refresh timer when player manually refreshes
        if (plugin.Configuration.EnableAutomaticRefresh)
        {
            // Clamp: a hand-edited config of 0/negative would otherwise make the countdown refresh every second.
        autoRefreshCountdown = Math.Max(5, plugin.Configuration.AutoRefreshIntervalSeconds);
        }

        TryProcessPendingRecruitment();
    }

    private void OnPFListFinalize(AddonEvent type, AddonArgs args)
    {
        //PassportCheckerReborn.Log.Information($"[PartyFinderManager] List CLOSED (partyCount={GetEffectivePartyCount()}, tracked={trackedPartyMemberCount}).");
        IsListOpen = false;
        pendingRecruitmentContentId = 0;
        StopAutoRefreshTimer();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Auto-Refresh
    // ═════════════════════════════════════════════════════════════════════════

    private void StartAutoRefreshTimer()
    {
        if (!plugin.Configuration.EnableAutomaticRefresh)
        {
            return;
        }

        // Clamp: a hand-edited config of 0/negative would otherwise make the countdown refresh every second.
        autoRefreshCountdown = Math.Max(5, plugin.Configuration.AutoRefreshIntervalSeconds);

        autoRefreshTimer?.Dispose();
        autoRefreshTimer = new System.Timers.Timer(1000); // tick every second
        autoRefreshTimer.AutoReset = true;
        autoRefreshTimer.Elapsed += OnAutoRefreshTick;
        autoRefreshTimer.Start();
    }

    private void StopAutoRefreshTimer()
    {
        if (autoRefreshTimer is not null)
        {
            autoRefreshTimer.Elapsed -= OnAutoRefreshTick;
            autoRefreshTimer.Stop();
            autoRefreshTimer.Dispose();
            autoRefreshTimer = null;
        }
    }

    private unsafe void OnAutoRefreshTick(object? sender, ElapsedEventArgs e)
    {
        // Don't refresh while detail pane is open
        if (IsDetailOpen || !IsListOpen)
        {
            StopAutoRefreshTimer();
            return;
        }

        if (autoRefreshCountdown > 1)
        {
            autoRefreshCountdown--;
            return;
        }

        // Reset countdown
        // Clamp: a hand-edited config of 0/negative would otherwise make the countdown refresh every second.
        autoRefreshCountdown = Math.Max(5, plugin.Configuration.AutoRefreshIntervalSeconds);

        // Request PF listing update on the framework thread (game API calls must
        // happen on the main thread). Uses AgentLookingForGroup as shown in the
        // DailyRoutines AutoRefreshPartyFinder reference.
        PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                AgentLookingForGroup.Instance()->RequestListingsUpdate();
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Auto-refresh failed.");
            }
        });
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Context Menu – "View Recruitment" (TODO item 10)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Registers the context-menu entry for "View Recruitment".</summary>
    public void RegisterContextMenu()
    {
        UnregisterContextMenu();
        PassportCheckerReborn.ContextMenu.OnMenuOpened += OnContextMenuOpened;
    }

    /// <summary>Unregisters the context-menu entry.</summary>
    public void UnregisterContextMenu()
    {
        PassportCheckerReborn.ContextMenu.OnMenuOpened -= OnContextMenuOpened;
    }

    private void OnContextMenuOpened(Dalamud.Game.Gui.ContextMenu.IMenuOpenedArgs args)
    {
        // Only add the menu item when right-clicking on a player character
        if (args.Target is not Dalamud.Game.Gui.ContextMenu.MenuTargetDefault target)
        {
            return;
        }

        if (target.TargetContentId == 0)
        {
            return;
        }

        args.AddMenuItem(new Dalamud.Game.Gui.ContextMenu.MenuItem
        {
            Name = new SeStringBuilder().AddText(Loc.T("View Recruitment")).Build(),
            OnClicked = OnViewRecruitmentClicked,
            IsEnabled = true,
        });
    }

    private unsafe void OnViewRecruitmentClicked(Dalamud.Game.Gui.ContextMenu.IMenuItemClickedArgs args)
    {
        if (args.Target is not Dalamud.Game.Gui.ContextMenu.MenuTargetDefault target)
        {
            return;
        }

        var contentId = target.TargetContentId;

        // Check if this player has a known PF listing
        if (!pfListingPlayerCache.TryGetValue(contentId, out var cached) || cached.ListingId == 0)
        {
            PassportCheckerReborn.ChatGui.Print(
                $"[PassportChecker] {Loc.T("No active Party Finder listing found for this player.")}");
            return;
        }

        // Store the pending selection
        pendingRecruitmentContentId = contentId;

        PassportCheckerReborn.Framework.RunOnFrameworkThread(() =>
        {
            try
            {
                var agent = AgentLookingForGroup.Instance();
                if (agent == null)
                {
                    PassportCheckerReborn.ChatGui.Print(
                        $"[PassportChecker] {Loc.T("Unable to open Party Finder.")}");
                    pendingRecruitmentContentId = 0;
                    return;
                }

                // If PF is already open, try to select the listing immediately
                if (IsListOpen)
                {
                    // Request a refresh first so the listing data is current
                    agent->RequestListingsUpdate();
                    // Selection will be attempted in OnPFListRefresh via TryProcessPendingRecruitment
                }
                else
                {
                    // Open the PF window; selection will be attempted in OnPFListSetup
                    agent->Show();
                }
            }
            catch (Exception ex)
            {
                PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] View Recruitment failed.");
                pendingRecruitmentContentId = 0;
            }
        });
    }

    /// <summary>
    /// Attempts to process a pending "View Recruitment" selection by finding
    /// the matching listing in the currently displayed PF list and selecting it.
    /// Called from PF list setup/refresh handlers.
    /// </summary>
    private unsafe void TryProcessPendingRecruitment()
    {
        var targetContentId = pendingRecruitmentContentId;
        if (targetContentId == 0)
        {
            return;
        }

        // Only attempt once per click
        pendingRecruitmentContentId = 0;

        if (!pfListingPlayerCache.TryGetValue(targetContentId, out var cached) || cached.ListingId == 0)
        {
            return;
        }

        var targetListingId = cached.ListingId;

        try
        {
            var agent = AgentLookingForGroup.Instance();
            if (agent == null)
            {
                return;
            }

            // Scan the agent's displayed listing IDs to find the target
            var listingIds = agent->Listings.ListingIds;
            var foundIndex = -1;
            for (var i = 0; i < listingIds.Length; i++)
            {
                if (listingIds[i] == targetListingId)
                {
                    foundIndex = i;
                    break;
                }
            }

            if (foundIndex < 0)
            {
                PassportCheckerReborn.ChatGui.Print(
                    $"[PassportChecker] {string.Format(Loc.T("Listing by {0} not found on the current page. It may have expired or be on a different category/page."), cached.Name)}");
                return;
            }

            // Fire a callback on the LookingForGroup addon to select this listing row.
            // The addon's callback handler expects: [0] = 3 (select listing action), [1] = listing index.
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroup");
            if (addonPtr.IsNull)
            {
                return;
            }

            var addon = (AtkUnitBase*)addonPtr.Address;
            var atkValues = stackalloc AtkValue[2];
            atkValues[0].Type = AtkValueType.Int;
            atkValues[0].Int = 3;
            atkValues[1].Type = AtkValueType.Int;
            atkValues[1].Int = foundIndex;
            addon->FireCallback(2, atkValues);

            PassportCheckerReborn.Log.Information(
                $"[PartyFinderManager] Selected listing {targetListingId} by {cached.Name} at index {foundIndex}.");
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[PartyFinderManager] Failed to select listing for View Recruitment.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // High-End Duty Detection (TODO item 13)
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the duty name directly from the LookingForGroupDetail addon's
    /// AtkValue array. Value at index 15 contains the duty name as a string.
    /// </summary>
    private unsafe void ReadDutyNameFromAddon()
    {
        CurrentDutyName = string.Empty;
        try
        {
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail", 1);
            if (addonPtr.IsNull)
            {
                return;
            }

            var addon = (AtkUnitBase*)addonPtr.Address;
            if (addon->AtkValuesCount > 15)
            {
                var atkValue = addon->AtkValues[15];
                // ValueType 6 = String, 8 = AllocatedString
                var typeId = (uint)atkValue.Type;
                if ((typeId == 6 || typeId == 8) && atkValue.String.HasValue)
                {
                    CurrentDutyName = atkValue.String.ToString() ?? string.Empty;
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Attempts to detect the duty from the current PF listing and checks whether
    /// it is a high-end duty (Savage, Ultimate, Extreme, Criterion).
    /// </summary>
    private void DetectCurrentDuty()
    {
        CurrentDutyId = 0;
        IsHighEndDuty = false;

        try
        {
            // Read DutyId directly from the intercepted Detailed struct — this is
            // the most reliable source and is always present after PopulateListingData fires.
            if (currentDetailedPost is { } post && post.DutyId > 0)
            {
                CurrentDutyId = post.DutyId;
                IsHighEndDuty = CheckHighEndDuty(CurrentDutyId);

                // Resolve the duty name from ContentFinderCondition using the RowId
                var cfcName = GetDutyNameFromId(CurrentDutyId);
                if (!string.IsNullOrEmpty(cfcName))
                {
                    //PassportCheckerReborn.Log.Info($"[PartyFinderManager] PF Duty Detected \"{cfcName}\".");
                    CurrentDutyName = cfcName;
                }

                // Also check the DutyCategory flag for HighEndDuty as a belt-and-braces check
                if (!IsHighEndDuty && post.Category.HasFlag(AgentLookingForGroup.DutyCategory.HighEndDuty))
                {
                    IsHighEndDuty = true;
                }
            }
            else
            {
                // Fall back to duty-name-based detection (e.g. hook hasn't fired yet)
                DetectHighEndFromName();
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to detect duty.");
            DetectHighEndFromName();
        }
    }

    /// <summary>
    /// Checks whether the duty name (from AtkValue[15]) indicates a high-end duty.
    /// Used as a fallback when <see cref="CurrentDutyId"/> is not available.
    /// </summary>
    private void DetectHighEndFromName()
    {
        if (string.IsNullOrEmpty(CurrentDutyName))
        {
            return;
        }

        var name = CurrentDutyName.ToLowerInvariant();
        IsHighEndDuty = name.Contains("savage") || name.Contains("ultimate") ||
                        name.Contains("extreme") || name.Contains("criterion") ||
                        name.Contains("unreal");
    }

    /// <summary>
    /// Checks whether the given duty ID corresponds to a high-end duty
    /// (Savage, Ultimate, Extreme, Criterion) using the game's ContentFinderCondition sheet.
    /// </summary>
    public static bool CheckHighEndDuty(uint dutyId)
    {
        try
        {
            var sheet = PassportCheckerReborn.DataManager.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null)
            {
                return false;
            }

            var row = sheet.GetRowOrDefault(dutyId);
            if (row == null)
            {
                return false;
            }

            var cfc = row.Value;

            // HighEndDuty flag is the primary indicator
            if (cfc.HighEndDuty)
            {
                return true;
            }

            // Also check ContentType for known high-end categories:
            // ContentType 5 = Raids, ContentType 28 = Ultimate Raids
            // We also check the name for keywords as a fallback
            var name = cfc.Name.ToString().ToLowerInvariant();
            if (name.Contains("savage") || name.Contains("ultimate") ||
                name.Contains("extreme") || name.Contains("criterion") ||
                name.Contains("unreal"))
            {
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to check high-end duty.");
            return false;
        }
    }

    /// <summary>
    /// Returns the duty name for the given <paramref name="dutyId"/> by looking up
    /// the matching <see cref="ContentFinderCondition"/> row (RowId == dutyId).
    /// Returns <c>null</c> if the row does not exist or the name is empty.
    /// </summary>
    public static string? GetDutyNameFromId(uint dutyId)
    {
        try
        {
            var sheet = PassportCheckerReborn.DataManager.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null)
            {
                return null;
            }

            var row = sheet.GetRowOrDefault(dutyId);
            if (row == null)
            {
                return null;
            }

            var name = row.Value.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to get duty name for id {0}.", dutyId);
            return null;
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Member Discovery
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Populates the member list from the intercepted <see cref="AgentLookingForGroup.Detailed"/>
    /// data. The leader is identified via the dedicated <c>LeaderContentId</c> field and their
    /// name/world are read directly from <c>LeaderString</c>/<c>HomeWorld</c> in the struct.
    /// The remaining 48-slot <c>MemberContentIds</c> array is scanned for other members, with
    /// names resolved from the PF listing cache (populated via
    /// <see cref="IPartyFinderGui.ReceiveListing"/>).
    /// </summary>
    private void RefreshMembers()
    {
        // Cancel any in-progress CharaCard resolution from a previous call
        resolveCts?.Cancel();
        resolveCts?.Dispose();
        resolveCts = null;

        ClearMembers();

        try
        {
            if (currentDetailedPost is not { } post)
            {
                return;
            }

            var classJobSheet = PassportCheckerReborn.DataManager.GetExcelSheet<ClassJob>();
            var worldSheet = PassportCheckerReborn.DataManager.GetExcelSheet<World>();

            PassportCheckerReborn.Log.Information(
                $"[PCR:Refresh] PopulateListingData post: ListingId={post.ListingId:X16} " +
                $"DutyId={post.DutyId} TotalSlots={post.TotalSlots} SlotsFilled={post.SlotsFilled} IsAlliance={post.IsAlliance}");

            // ── Leader (slot 0) ─────────────────────────────────────────────
            // LeaderContentId is a dedicated field in Detailed; the leader name
            // and home world are also embedded directly so no cache hit is needed.
            var leaderCid = post.LeaderContentId;
            if (leaderCid != 0)
            {
                var leaderName = post.LeaderString;
                var leaderWorldName = worldSheet?.GetRowOrDefault(post.HomeWorld)?.Name.ToString() ?? string.Empty;

                // If the embedded name is empty, try the cache (cross-realm join edge case)
                var leaderFromPlayerTrack = false;
                if (string.IsNullOrEmpty(leaderName))
                {
                    var cached = ResolvePlayerFromCache(leaderCid);
                    leaderName = cached?.Name ?? $"{UnresolvedNamePrefix}{leaderCid:X16}";
                    leaderWorldName = cached?.World ?? leaderWorldName;
                    leaderFromPlayerTrack = cached?.Source == NameSource.PlayerTrack;
                }

                // Cache the leader so CharaCard fallback and context-menu features work
                if (!string.IsNullOrEmpty(leaderName) && !leaderName.StartsWith(UnresolvedNamePrefix))
                {
                    var existingId = pfListingPlayerCache.TryGetValue(leaderCid, out var prev) ? prev.ListingId : post.ListingId;
                    pfListingPlayerCache[leaderCid] = (leaderName, post.HomeWorld, existingId);
                    plugin.CidCache.Set(leaderCid, leaderName, post.HomeWorld, leaderWorldName);
                }

                // Find the leader's job from the MemberContentIds/Jobs arrays
                var leaderJobAbbreviation = "???";
                for (var j = 0; j < 48; j++)
                {
                    if (post.MemberContentIds[j] == leaderCid)
                    {
                        var leaderJobId = post.Jobs[j];
                        leaderJobAbbreviation = classJobSheet?.GetRowOrDefault(leaderJobId)?.Abbreviation.ToString() ?? "???";
                        break;
                    }
                }

                AddMember(new Windows.PartyMemberInfo(leaderName, leaderWorldName, leaderJobAbbreviation, leaderCid)
                {
                    FromPlayerTrack = leaderFromPlayerTrack,
                });
            }
            else
            {
                PassportCheckerReborn.Log.Warning("[PCR:Refresh] LeaderContentId is 0 — listing is expired or invalid, skipping member scan.");
                return;
            }

            // ── Other members (MemberContentIds[0..47]) ─────────────────────
            // Scan all slots up to TotalSlots — MemberContentIds is indexed by slot position,
            // so members can occupy any slot within [0, TotalSlots). Reading beyond TotalSlots
            // produces garbage data; using SlotsFilled would skip members in higher-numbered slots.
            var slotsToScan = (post.TotalSlots > 0 && post.TotalSlots <= 48) ? (int)post.TotalSlots : 48;
            var nonZeroSlots = 0;
            for (var i = 0; i < slotsToScan; i++)
            {
                var contentId = post.MemberContentIds[i];
                if (contentId == 0)
                {
                    continue;
                }

                nonZeroSlots++;
                if (contentId == leaderCid)
                {
                    continue;
                }

                var jobId = post.Jobs[i];
                var jobAbbreviation = classJobSheet?.GetRowOrDefault(jobId)?.Abbreviation.ToString() ?? "???";

                var resolved = ResolvePlayerFromCache(contentId);
                var name = resolved?.Name ?? $"{UnresolvedNamePrefix}{contentId:X16}";
                var world = resolved?.World ?? string.Empty;

                // Provenance flows straight from the resolving source — no name-matching needed.
                AddMember(new Windows.PartyMemberInfo(name, world, jobAbbreviation, contentId)
                {
                    FromPlayerTrack = resolved?.Source == NameSource.PlayerTrack,
                });
            }

            var members = MembersSnapshot();
            PassportCheckerReborn.Log.Information(
                $"[PCR:Refresh] MemberContentIds scan: {nonZeroSlots} non-zero slots, {members.Length} members added total.");

            if (members.Length > 0)
            {
                // If any members were not resolved from cache, try CharaCard lookup
                var hasUnresolved = false;
                foreach (var m in members)
                {
                    if (m.Name.StartsWith(UnresolvedNamePrefix))
                    {
                        hasUnresolved = true;
                        break;
                    }
                }

                var playerTrackUsable = plugin.Configuration.EnablePlayerTrackIntegration
                    && plugin.PlayerTrackService?.IsUsable == true;
                var charaCardReady = charaCardPacketHandlerHook is { IsEnabled: true };
                var reverifyNeeded = charaCardReady && plugin.Configuration.EnableStaleNameReverification
                    && AnyMemberNeedsReverify();

                if ((hasUnresolved || reverifyNeeded) && (charaCardReady || playerTrackUsable))
                {
                    resolveCts = new CancellationTokenSource();
                    _ = ResolveUnresolvedMembersAsync(resolveCts.Token);
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[PartyFinderManager] Failed to read member data.");
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the string looks like a plausible FFXIV name
    /// part (only letters, hyphens, and apostrophes).
    /// </summary>
    private static bool IsPlausibleNamePart(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsLetter(c) && c != '-' && c != '\'')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Manually refresh the member list (e.g. on overlay open).</summary>
    public void RequestRefresh() => RefreshMembers();

    /// <summary>Manually triggers a live re-read of the blacklist string array and saves the result.</summary>
    public void ForceRefreshBlacklist() => ReadBlacklistFromAddon();

    /// <summary>
    /// Checks if a player is on the local user's blacklist.
    /// </summary>
    public bool IsBlacklisted(string name, string world)
    {
        if (!plugin.Configuration.EnableBlacklistFeature)
        {
            return false;
        }

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var result = (!string.IsNullOrEmpty(world) && blacklistedPlayers.ContainsKey($"{name}@{world}"))
                     || blacklistedPlayers.ContainsKey(name);
        //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] IsBlacklisted(\"{name}\"@\"{world}\") = {result}  (cache size={blacklistedPlayers.Count})");
        return result;
    }

    /// <summary>
    /// Seeds <see cref="blacklistedPlayers"/> from the persisted <see cref="BlacklistCache"/>
    /// so the blacklist is available immediately on plugin load, before the first live
    /// read from <c>BlackListStringArray</c>.
    /// </summary>
    private void SeedBlacklistFromCache()
    {
        foreach (var key in plugin.BlacklistCache.GetAllKeys())
        {
            blacklistedPlayers[key] = true;
        }

        //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] Seeded {blacklistedPlayers.Count} entries from BlacklistCache.");
    }

    /// <summary>
    /// Reads all blacklisted player entries from <see cref="BlackListStringArray"/> and
    /// caches them as "Name@World" keys for fast per-frame lookup.
    /// The string array is always present in memory; the BlackList addon does not need
    /// to be open.
    /// </summary>
    private unsafe void ReadBlacklistFromAddon()
    {
        var newEntries = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var array = BlackListStringArray.Instance();
            if (array == null)
            {
                return;
            }

            var names = array->PlayerNames;
            var worlds = array->Homeworlds;

            for (var i = 0; i < names.Length; i++)
            {
                var name = names[i].ToString();
                if (string.IsNullOrEmpty(name))
                {
                    break;
                }

                var world = worlds[i].ToString() ?? string.Empty;
                var key = string.IsNullOrEmpty(world) ? name : $"{name}@{world}";
                //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] Blacklist entry [{i}]: \"{key}\"");
                newEntries[key] = true;
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[PartyFinderManager] Failed to read BlackList string array.");
        }

        blacklistedPlayers.Clear();
        foreach (var (k, v) in newEntries)
        {
            blacklistedPlayers[k] = v;
        }

        PassportCheckerReborn.Log.Debug($"[PartyFinderManager] Blacklist cache updated: {blacklistedPlayers.Count} entr{(blacklistedPlayers.Count == 1 ? "y" : "ies")}.");

        // Persist the live snapshot to disk so it survives plugin reloads.
        plugin.BlacklistCache.ReplaceAll(newEntries.Keys);
    }

    private void OnBlacklistAddonUpdated(AddonEvent type, AddonArgs args)
    {
        //PassportCheckerReborn.Log.Debug($"[PartyFinderManager] OnBlacklistAddonUpdated fired (event={type}).");
        ReadBlacklistFromAddon();
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Dispose
    // ═════════════════════════════════════════════════════════════════════════

    public void Dispose()
    {
        // Cancel any in-progress CharaCard resolution
        resolveCts?.Cancel();
        resolveCts?.Dispose();

        StopAutoRefreshTimer();
        UnregisterContextMenu();

        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailSetup);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailRefresh);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFDetailFinalize);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListSetup);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListRefresh);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnPFListFinalize);
        PassportCheckerReborn.AddonLifecycle.UnregisterListener(OnBlacklistAddonUpdated);

        PassportCheckerReborn.PartyFinderGui.ReceiveListing -= OnReceiveListing;

        populateListingHook?.Dispose();
        charaCardPacketHandlerHook?.Dispose();
        showLogMessageHook?.Dispose();
        charaCardRequestGate.Dispose();

        ClearMembers();
        pfListingPlayerCache.Clear();
    }
}
