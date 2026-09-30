using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.UI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

/// <summary>
/// The member-info overlay that appears alongside the Party Finder detail pane.
/// It lists all current party-finder members (fetched via
/// <see cref="Services.PartyFinderManager"/>) and, via two shared buttons below the
/// player list, performs batch Tomestone / FFLogs lookups for every member at once.
/// </summary>
public class PFWindow(PassportCheckerReborn plugin) : Window("PF Member Info##PFCheckerOverlay",
           ImGuiWindowFlags.NoTitleBar |
               ImGuiWindowFlags.NoResize |
               ImGuiWindowFlags.NoMove |
               ImGuiWindowFlags.NoScrollbar |
               ImGuiWindowFlags.AlwaysAutoResize), IDisposable
{
    private readonly PassportCheckerReborn plugin = plugin;

    // Per-member caches keyed by ContentId (player identity), NOT row index — so when the listing's roster
    // shifts (a member joins/leaves) each player keeps their own result and only genuinely-new members are
    // fetched; nothing is misattributed to a shifted row and the display doesn't flicker. Cleared only when
    // the DUTY changes (the cached encounter data no longer applies).
    // ConcurrentDictionary: the background batch tasks write entries (progressively) while the UI thread reads
    // them each frame.
    private ConcurrentDictionary<ulong, EncounterParseResult?> fflogsEncounterCache = new();
    private bool fflogsBatchInProgress;

    private ConcurrentDictionary<ulong, TomestoneCharacterInfo?> tomestoneInfoCache = new();
    private bool tomestoneBatchInProgress;

    // The duty the caches currently hold data for. When it changes the caches are cleared (the per-player
    // encounter results no longer apply). Tracks the NAME too so id-0 content that is mapped only by name and
    // shares dutyId 0 across different duties still invalidates correctly.
    private uint lastFetchDutyId;
    private string lastFetchDutyName = string.Empty;

    // Cached size of this overlay window from the previous frame, used for clamping in PreDraw
    private Vector2 lastWindowSize = new(300f, 200f);

    private M3Style.Scope? theme;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override unsafe void PreDraw()
    {
        theme = M3Style.Push(compact: true);

        // Position this window to the left or right of the PF Details addon.
        //
        // Uses GameGui.GetAddonByName to find the LookingForGroupDetail addon,
        // reads its position and size, and sets the ImGui window position accordingly.
        try
        {
            var addonPtr = PassportCheckerReborn.GameGui.GetAddonByName("LookingForGroupDetail", 1);
            if (!addonPtr.IsNull)
            {
                var addon = (AtkUnitBase*)addonPtr.Address;
                if (addon->IsVisible)
                {
                    var addonX = addon->X;
                    var addonY = addon->Y;
                    var addonWidth = addon->GetScaledWidth(true);
                    var addonHeight = addon->GetScaledHeight(true);

                    // Get the ImGui viewport offset for coordinate conversion
                    var vpPos = ImGui.GetMainViewport().Pos;

                    var overlayY = vpPos.Y + addonY;

                    if (plugin.Configuration.ShowOverlayOnLeftSide)
                    {
                        // Anchor the top-right corner of the overlay to the left edge of the addon
                        // so the window grows leftward and does not cover LookingForGroupDetail.
                        var anchorX = vpPos.X + addonX - 10;

                        // Clamp so the left edge of the window (anchorX - windowWidth) stays on screen.
                        // Use the previous frame's size for estimation; falls back to a safe default on first frame.
                        var windowWidth = lastWindowSize.X;
                        var vpSize = ImGui.GetMainViewport().Size;
                        var minAnchorX = vpPos.X + windowWidth;
                        var maxAnchorX = vpPos.X + vpSize.X;
                        anchorX = Math.Clamp(anchorX, minAnchorX, maxAnchorX);

                        ImGui.SetNextWindowPos(new Vector2(anchorX, overlayY), ImGuiCond.Always, new Vector2(1f, 0f));
                        Position = null;
                    }
                    else
                    {
                        // Place overlay to the right of the addon, clamped to screen edge.
                        var vpSize = ImGui.GetMainViewport().Size;
                        var overlayX = vpPos.X + addonX + addonWidth + 10;
                        var windowWidth = lastWindowSize.X;
                        overlayX = Math.Min(overlayX, vpPos.X + vpSize.X - windowWidth);
                        overlayX = Math.Max(overlayX, vpPos.X);
                        Position = new Vector2(overlayX, overlayY);
                    }
                    return;
                }
            }
        }
        catch (Exception)
        {
        }

        // If the addon isn't found or isn't visible, don't force a position
        // (let the window float freely so it's still usable during development).
    }

    public override void Draw()
    {
        // Body text follows the text size setting.
        using var bodyFont = ImRaii.PushFont(M3.Body);

        var cfg = plugin.Configuration;

        // Clear the caches only when the DUTY changes — the per-player results are keyed to that duty's
        // encounter. A roster change (member joins/leaves) keeps the same duty, so cached players are kept and
        // only new ones get fetched. Key on (id, name) so id-0 mapped-by-name content also invalidates; ignore
        // the fully-blank between-listings state (id 0 AND no name) so we don't wipe on it.
        var dutyId = plugin.PartyFinderManager.CurrentDutyId;
        var dutyName = plugin.PartyFinderManager.CurrentDutyName;
        if ((dutyId != 0 || !string.IsNullOrEmpty(dutyName))
            && (dutyId != lastFetchDutyId || !string.Equals(dutyName, lastFetchDutyName, StringComparison.Ordinal)))
        {
            lastFetchDutyId = dutyId;
            lastFetchDutyName = dutyName;
            fflogsEncounterCache = new();
            tomestoneInfoCache = new();
        }

        if (!cfg.ShowMemberInfoOverlay || !plugin.PartyFinderManager.IsDetailOpen)
        {
            IsOpen = false;
            return;
        }

        // If "Only Show for High-End Duties" is enabled, check the duty type
        if (cfg.OnlyShowOverlayForHighEndDuties && !plugin.PartyFinderManager.IsHighEndDuty)
        {
            // Hide if we positively know it's not high-end (either via ID or name)
            if (plugin.PartyFinderManager.IsDetailOpen &&
                (plugin.PartyFinderManager.CurrentDutyId > 0 ||
                 !string.IsNullOrEmpty(plugin.PartyFinderManager.CurrentDutyName)))
            {
                OverlayWidgets.EmptyState(FontAwesomeIcon.Filter, Loc.T("Not a high-end duty."));
                return;
            }
        }

        var members = plugin.PartyFinderManager.CurrentMembers;

        OverlayWidgets.Header(FontAwesomeIcon.Users, Loc.T("Member Info"), string.IsNullOrWhiteSpace(dutyName) ? null : dutyName);
        ImGui.Dummy(new Vector2(0f, M3.Space1));

        if (members.Count == 0)
        {
            OverlayWidgets.EmptyState(FontAwesomeIcon.Search, Loc.T("No party finder listing selected."),
                Loc.T("Open a PF detail window to see member info."));
            return;
        }

        // ── Player rows (info + cached data, no per-row buttons) ────────────
        var hasTomestone = cfg.EnableTomestoneIntegration && !string.IsNullOrEmpty(cfg.TomestoneApiKey);
        var hasFFLogs = cfg.EnableFFLogsIntegrationOverlay && !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret);

        var columnCount = 1 + (hasTomestone ? 1 : 0) + (hasFFLogs ? 1 : 0);

        if (ImGui.BeginTable("##members_table", columnCount, OverlayWidgets.TableFlags))
        {
            ImGui.TableSetupColumn("Player", ImGuiTableColumnFlags.WidthFixed);
            if (hasTomestone)
            {
                ImGui.TableSetupColumn("Tomestone", ImGuiTableColumnFlags.WidthFixed);
            }

            if (hasFFLogs)
            {
                ImGui.TableSetupColumn("FFLogs", ImGuiTableColumnFlags.WidthFixed);
            }

            OverlayWidgets.BeginHeaderRow();
            OverlayWidgets.HeaderCell(Loc.T("Name"));
            if (hasTomestone)
            {
                OverlayWidgets.HeaderCell("Tomestone");
            }

            if (hasFFLogs)
            {
                OverlayWidgets.HeaderCell("FFLogs");
            }

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                DrawMemberRow(member, i, cfg, hasTomestone, hasFFLogs);
            }

            ImGui.EndTable();
        }

        var isResolving = plugin.PartyFinderManager.HasUnresolvedMembers;

        // Auto-fetch FFLogs once names are resolved (option). Only members with NO cached entry yet — so a
        // failed lookup (which leaves a FetchFailed entry) isn't auto-retried in a loop, and a member who
        // joins the listing later is picked up automatically without wiping everyone else.
        if (cfg.AutoFetchFFLogsWhenResolved && hasFFLogs && !fflogsBatchInProgress && !isResolving)
        {
            var toFetch = FfMembersToFetch(members, includeFailed: false);
            if (toFetch.Count > 0)
            {
                fflogsBatchInProgress = true;
                _ = FetchFFLogsForAsync(toFetch);
            }
        }

        // ── Shared Tomestone / FFLogs buttons below all rows ────────────────
        if (cfg.EnableTomestoneIntegration || cfg.EnableFFLogsIntegrationOverlay)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            DrawLookupButtons(members, cfg, isResolving);
        }

        // While a batch is running, cleared/parsed rows appear first and progression fills in a moment later;
        // a footer note makes clear that more data is still coming so the early rows don't read as "done".
        if (fflogsBatchInProgress || tomestoneBatchInProgress)
        {
            ImGui.Dummy(new Vector2(0f, M3.Space1));
            OverlayWidgets.StatusLine(FontAwesomeIcon.HourglassHalf, Loc.T("Fetching more data…"));
        }

        // Capture this frame's window size for use in PreDraw() clamping next frame
        lastWindowSize = ImGui.GetWindowSize();
    }

    /// <summary>The batch lookup buttons, one per enabled integration.</summary>
    private void DrawLookupButtons(IReadOnlyList<PartyMemberInfo> members, Configuration cfg, bool isResolving)
    {
        if (cfg.EnableTomestoneIntegration)
        {
            var hasKey = !string.IsNullOrEmpty(cfg.TomestoneApiKey);

            // Disabled while resolving/loading, or when every member already has data.
            var tsToFetch = hasKey ? TsMembersToFetch(members) : [];
            if (LookupButton("##ts_all", Loc.T("Tomestone Lookup"), hasKey, Loc.T("Tomestone API Key Needed"),
                    isResolving, tomestoneBatchInProgress, tsToFetch.Count == 0))
            {
                tomestoneBatchInProgress = true;
                _ = FetchTomestoneForAsync(tsToFetch);
            }

            ImguiTooltips.HoveredTooltip(!hasKey
                ? Loc.T("Configure your Tomestone API key in Settings → Tomestone.")
                : isResolving ? Loc.T("Waiting for player names to be resolved…")
                : tomestoneBatchInProgress ? Loc.T("Looking up Tomestone data for all players…")
                : tsToFetch.Count == 0 ? Loc.T("Tomestone data already loaded for this listing")
                : Loc.T("Look up Tomestone data for all players"));

            if (cfg.EnableFFLogsIntegrationOverlay)
            {
                ImGui.SameLine(0f, M3.Space2);
            }
        }

        if (cfg.EnableFFLogsIntegrationOverlay)
        {
            var hasCredentials = !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret);

            // Disabled while resolving/loading, or when every member already has data. Members whose last
            // lookup FAILED are included again so the button re-enables for a manual retry.
            var ffToFetch = hasCredentials ? FfMembersToFetch(members, includeFailed: true) : [];
            if (LookupButton("##ff_all", Loc.T("FFLogs Lookup"), hasCredentials, Loc.T("FFLogs API Key Needed"),
                    isResolving, fflogsBatchInProgress, ffToFetch.Count == 0))
            {
                fflogsBatchInProgress = true;
                _ = FetchFFLogsForAsync(ffToFetch);
            }

            ImguiTooltips.HoveredTooltip(!hasCredentials
                ? Loc.T("Configure your FFLogs credentials in Settings → FFLogs.")
                : isResolving ? Loc.T("Waiting for player names to be resolved…")
                : fflogsBatchInProgress ? Loc.T("Looking up FFLogs data for all players…")
                : ffToFetch.Count == 0 ? Loc.T("FFLogs data already loaded for this listing")
                : Loc.T("Look up FFLogs data for all players"));
        }
    }

    /// <summary>
    /// One batch lookup button. It stays disabled until the integration is configured and every player's
    /// name is resolved, while its lookup is running, and once every member has data. Returns true when clicked.
    /// </summary>
    private static bool LookupButton(string id, string label, bool configured, string unconfiguredLabel,
        bool isResolving, bool inProgress, bool allLoaded)
    {
        var (text, icon) = !configured ? (unconfiguredLabel, FontAwesomeIcon.Key)
            : inProgress || isResolving ? (label, FontAwesomeIcon.HourglassHalf)
            : allLoaded ? (label, FontAwesomeIcon.Check)
            : (label, FontAwesomeIcon.Search);

        return M3Widgets.Button(id, text, M3ButtonStyle.Tonal, icon,
            enabled: configured && !isResolving && !inProgress && !allLoaded);
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg, bool hasTomestone, bool hasFFLogs)
    {
        ImGui.TableNextRow();
        using var id = ImRaii.PushId(index);

        var isBlacklisted = plugin.PartyFinderManager.IsBlacklisted(member.Name, member.World);

        // ── Column 0: Job icon + player name + badges ─────────────────────
        ImGui.TableSetColumnIndex(0);

        if (cfg.ShowPartyJobIcons && !string.IsNullOrWhiteSpace(member.JobAbbreviation))
        {
            OverlayWidgets.JobIcon(member.JobAbbreviation);
            ImGui.SameLine();
        }

        // Player label
        var isUnresolved = member.Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix)
            || member.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix);
        var isResolved = !isUnresolved;
        // Anonymized label used for private/unresolved members and, unless the user opts in,
        // for resolved members too. The Private badge below distinguishes private members,
        // so the name itself never repeats the word "Private".
        var anonymousName = $"{Loc.T("Player")} {index + 1}";
        string displayName;
        if (member.IsPrivate)
        {
            displayName = anonymousName;
        }
        else if (cfg.ShowResolvedPlayerNames && isResolved)
        {
            displayName = $"{member.Name}@{member.World}";
        }
        else
        {
            displayName = anonymousName;
        }

        // Resolved names link to their FFLogs page (built from name + world, zero FFLogs API points) and
        // hover-show provenance + a click hint.
        var fflogsUrl = !member.IsPrivate && isResolved ? FFLogsService.GetCharacterPageUrl(member.Name, member.World) : null;

        ImGui.AlignTextToFramePadding();
        if (member.IsPrivate)
        {
            ImGui.TextColored(OverlayWidgets.Muted, displayName);
        }
        else if (fflogsUrl is not null)
        {
            OverlayWidgets.LinkText(displayName, fflogsUrl,
                drawTooltip: () => DrawNameProvenanceTooltip(member, showClickHint: true));
        }
        else
        {
            ImGui.TextUnformatted(displayName);
        }

        if (member.IsPrivate)
        {
            ImGui.SameLine();
            M3Badge.Draw(Loc.T("Private"), M3.Scheme.OnSurfaceVariant, Loc.T("Adventure plate is hidden or unavailable"));
        }

        if (isBlacklisted)
        {
            ImGui.SameLine();
            M3Badge.Draw("BL", M3.Scheme.Error, Loc.T("On your blacklist"));
        }

        // ── PlayerTrack provenance badge (full detail is on the name tooltip) ──
        if (member.FromPlayerTrack)
        {
            ImGui.SameLine();
            M3Badge.Draw("PT", M3.Scheme.Info);
            if (ImGui.IsItemHovered())
            {
                DrawNameProvenanceTooltip(member);
            }
        }

        // ── Column 1: Tomestone data ──────────────────────────────────────
        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (member.IsPrivate)
            {
                PrivateCell();
            }
            else if (tomestoneInfoCache.TryGetValue(member.ContentId, out var cachedTs))
            {
                OverlayWidgets.TomestoneCell(cachedTs);
            }
            else if (tomestoneBatchInProgress)
            {
                OverlayWidgets.Pending();
            }
        }

        // ── Column 2: FFLogs data ─────────────────────────────────────────
        if (hasFFLogs)
        {
            ImGui.TableNextColumn();
            if (member.IsPrivate)
            {
                PrivateCell();
            }
            else if (fflogsEncounterCache.TryGetValue(member.ContentId, out var cachedFf))
            {
                OverlayWidgets.FFLogsCell(cachedFf, member);
            }
            else if (fflogsBatchInProgress)
            {
                OverlayWidgets.Pending();
            }
        }
    }

    /// <summary>A data cell for a private slot, which can never be looked up.</summary>
    private static void PrivateCell()
    {
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(OverlayWidgets.Muted, "-");
    }

    // ── Cache helpers (all caches keyed by member ContentId) ────────────────

    /// <summary>Members eligible for a lookup: a resolved real name, not private, with a usable ContentId
    /// (the cache key).</summary>
    private static bool IsLookupEligible(PartyMemberInfo m) =>
        !m.IsPrivate
        && m.ContentId != 0
        && !string.IsNullOrWhiteSpace(m.Name)   // matches the service's own empty-name skip; avoids a member
                                                 // that never gets a cached result being re-fetched every frame
        && !m.Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix, StringComparison.Ordinal)
        && !m.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix, StringComparison.Ordinal);

    /// <summary>Eligible members whose FFLogs result isn't cached yet — plus, when <paramref name="includeFailed"/>,
    /// those whose last lookup failed (so a manual retry re-covers them). This is the set a fetch should cover.</summary>
    private List<PartyMemberInfo> FfMembersToFetch(IReadOnlyList<PartyMemberInfo> members, bool includeFailed)
    {
        var list = new List<PartyMemberInfo>();
        foreach (var m in members)
        {
            if (!IsLookupEligible(m))
            {
                continue;
            }

            if (!fflogsEncounterCache.TryGetValue(m.ContentId, out var cached))
            {
                list.Add(m);
            }
            else if (includeFailed && cached?.FetchFailed == true)
            {
                list.Add(m);
            }
        }

        return list;
    }

    /// <summary>Eligible members whose Tomestone info isn't cached yet.</summary>
    private List<PartyMemberInfo> TsMembersToFetch(IReadOnlyList<PartyMemberInfo> members)
    {
        var list = new List<PartyMemberInfo>();
        foreach (var m in members)
        {
            if (IsLookupEligible(m) && !tomestoneInfoCache.ContainsKey(m.ContentId))
            {
                list.Add(m);
            }
        }

        return list;
    }

    /// <summary>
    /// Fetches FFLogs data for the given members and stores each result in the cache keyed by ContentId, so a
    /// later roster change keeps every other player's data and only genuinely-new members are re-queried.
    /// Uses an encounter-specific batch when the duty is mapped, else a general zone-parse fallback. Renders
    /// progressively via the per-member callback.
    /// </summary>
    private async Task FetchFFLogsForAsync(List<PartyMemberInfo> toFetch)
    {
        // Capture the cache instance now: if the duty changes mid-fetch the UI thread swaps the field for a
        // fresh dictionary, and this (old-duty) task must keep writing into the OLD one — which is then
        // discarded — so it can never leak old-duty results into the new duty's cache.
        var cache = fflogsEncounterCache;
        try
        {
            var memberData = new List<(string Name, string World, string JobAbbreviation)>(toFetch.Count);
            foreach (var m in toFetch)
            {
                memberData.Add((m.Name, m.World, m.JobAbbreviation));
            }

            var encounterIds = FFLogsService.GetEncounterIdsForDuty(
                plugin.PartyFinderManager.CurrentDutyId,
                plugin.PartyFinderManager.CurrentDutyName);

            if (encounterIds.HasValue)
            {
                var results = await plugin.FFLogsService.GetDutyEncounterDataForAllAsync(
                    memberData,
                    plugin.PartyFinderManager.CurrentDutyId,
                    plugin.PartyFinderManager.CurrentDutyName,
                    onMemberUpdated: (index, result) =>
                    {
                        if (index >= 0 && index < toFetch.Count)
                        {
                            cache[toFetch[index].ContentId] = result;
                        }
                    });

                foreach (var (index, result) in results)
                {
                    if (index >= 0 && index < toFetch.Count)
                    {
                        cache[toFetch[index].ContentId] = result;
                    }
                }
            }
            else
            {
                // Fallback: batched general zone parse in one request.
                var averages = await plugin.FFLogsService.GetZoneAveragesForAllAsync(memberData);
                for (var i = 0; i < toFetch.Count; i++)
                {
                    cache[toFetch[i].ContentId] =
                        averages.TryGetValue(i, out var r) ? r : new EncounterParseResult(false, false, 0, null, null);
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[OverlayWindow] FFLogs batch lookup failed.");

            // Mark any member left without a result as a retryable failure, so auto-fetch doesn't loop on them
            // (they now have an entry) while the button — which includes failed members — can still retry.
            foreach (var m in toFetch)
            {
                if (!cache.ContainsKey(m.ContentId))
                {
                    cache[m.ContentId] = new EncounterParseResult(false, true, 0, null, null) { FetchFailed = true };
                }
            }
        }
        finally
        {
            fflogsBatchInProgress = false;
        }
    }

    /// <summary>
    /// Fetches Tomestone info for the given members, storing each result in the cache keyed by ContentId.
    /// Passes the current duty name so the API can return encounter-specific data.
    /// </summary>
    private async Task FetchTomestoneForAsync(List<PartyMemberInfo> toFetch)
    {
        // Capture the cache instance (see FetchFFLogsForAsync) so a mid-fetch duty change can't leak results
        // into the new duty's cache.
        var cache = tomestoneInfoCache;
        try
        {
            var dutyName = plugin.PartyFinderManager.CurrentDutyName;

            foreach (var member in toFetch)
            {
                try
                {
                    var info = await plugin.TomestoneService.GetCharacterInfoAsync(
                        member.Name, member.World, dutyName);
                    cache[member.ContentId] = info;
                }
                catch (Exception ex)
                {
                    PassportCheckerReborn.Log.Warning(ex,
                        $"[OverlayWindow] Tomestone lookup failed for {member.Name}@{member.World}");
                    cache[member.ContentId] = null;
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[OverlayWindow] Tomestone batch lookup failed.");
        }
        finally
        {
            tomestoneBatchInProgress = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PlayerTrack helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tooltip shown when hovering a resolved member's name (or the PT badge): the name's source,
    /// how old the cached data is, and any previous names recorded for this Content ID. Reads the
    /// in-memory CID cache — no IO.
    /// </summary>
    private void DrawNameProvenanceTooltip(PartyMemberInfo member, bool showClickHint = false)
    {
        var entry = plugin.CidCache.TryGet(member.ContentId, out var e) && e is not null ? e : null;
        if (entry is null && !showClickHint)
        {
            return;
        }

        ImGui.BeginTooltip();

        if (entry is not null)
        {
            // Source line dropped — the [PT] badge already marks PlayerTrack-sourced names, and live is the
            // default, so it was redundant.
            if (entry.LastSeen != DateTime.MinValue)
            {
                ImGui.TextUnformatted($"{Loc.T("Data age")}: {FormatAge(DateTime.UtcNow - entry.LastSeen)} ({entry.LastSeen.ToLocalTime():yyyy-MM-dd HH:mm})");
                if (DateTime.UtcNow - entry.LastSeen >= TimeSpan.FromDays(Math.Max(1, plugin.Configuration.StaleNameThresholdDays)))
                {
                    ImGui.TextColored(OverlayWidgets.Muted, Loc.T("This name is old and may be out of date."));
                }
            }

            if (entry.PreviousNames is { Count: > 0 } previous)
            {
                ImGui.Separator();
                ImGui.TextUnformatted(Loc.T("Previously seen as:"));
                foreach (var p in previous)
                {
                    var world = string.IsNullOrEmpty(p.WorldName) ? string.Empty : $"@{p.WorldName}";
                    var when = p.SeenUntil != DateTime.MinValue
                        ? "  (" + string.Format(Loc.T("until {0}"), p.SeenUntil.ToLocalTime().ToString("yyyy-MM-dd")) + ")"
                        : string.Empty;
                    ImGui.BulletText($"{p.Name}{world}{when}");
                }
            }
        }

        if (showClickHint)
        {
            if (entry is not null)
            {
                ImGui.Separator();
            }

            ImGui.TextColored(OverlayWidgets.Muted, Loc.T("Click to open FFLogs page"));
        }

        ImGui.EndTooltip();
    }

    private static string FormatAge(TimeSpan span)
    {
        if (span.TotalDays >= 365)
        {
            return string.Format(Loc.T("{0}y ago"), (int)(span.TotalDays / 365));
        }

        if (span.TotalDays >= 1)
        {
            return string.Format(Loc.T("{0}d ago"), (int)span.TotalDays);
        }

        if (span.TotalHours >= 1)
        {
            return string.Format(Loc.T("{0}h ago"), (int)span.TotalHours);
        }

        if (span.TotalMinutes >= 1)
        {
            return string.Format(Loc.T("{0}m ago"), (int)span.TotalMinutes);
        }

        return Loc.T("just now");
    }
}

/// <summary>Data object representing a single party member seen in a PF listing or party.</summary>
public record PartyMemberInfo(
    string Name,
    string World,
    string JobAbbreviation,
    ulong ContentId = 0,
    bool IsPrivate = false,
    ushort WorldId = 0)
{
    /// <summary>True when this member's name was resolved from the PlayerTrack database
    /// rather than from PF packets / adventure plate. The last-seen/previous-name detail for the
    /// tooltip is fetched on demand from <c>PlayerTrackService</c> by ContentId.</summary>
    public bool FromPlayerTrack { get; init; }
}
