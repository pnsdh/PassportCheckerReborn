using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using PassportCheckerReborn.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override unsafe void PreDraw()
    {
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
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), Loc.T("Not a high-end duty."));
                return;
            }
        }

        var members = plugin.PartyFinderManager.CurrentMembers;

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), $"{Loc.T("PF Member Info")} - {plugin.PartyFinderManager.CurrentDutyName}");
        ImGui.Separator();
        ImGui.Spacing();

        if (members.Count == 0)
        {
            ImGui.TextUnformatted(Loc.T("No party finder listing selected."));
            ImGui.TextUnformatted(Loc.T("Open a PF detail window to see member info."));
            return;
        }

        // ── Player rows (info + cached data, no per-row buttons) ────────────
        var hasTomestone = cfg.EnableTomestoneIntegration && !string.IsNullOrEmpty(cfg.TomestoneApiKey);
        var hasFFLogs = cfg.EnableFFLogsIntegrationOverlay && !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret);

        var columnCount = 1 + (hasTomestone ? 1 : 0) + (hasFFLogs ? 1 : 0);
        var tableFlags = ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.NoHostExtendX;

        if (ImGui.BeginTable("##members_table", columnCount, tableFlags))
        {
            ImGui.TableSetupColumn(Loc.T("Name"), ImGuiTableColumnFlags.WidthFixed);
            if (hasTomestone)
            {
                ImGui.TableSetupColumn("Tomestone", ImGuiTableColumnFlags.WidthFixed);
            }

            if (hasFFLogs)
            {
                ImGui.TableSetupColumn("FFLogs", ImGuiTableColumnFlags.WidthFixed);
            }

            ImGui.TableHeadersRow();

            for (var i = 0; i < members.Count; i++)
            {
                var member = members[i];
                DrawMemberRow(member, i, cfg, hasTomestone, hasFFLogs);
            }

            ImGui.EndTable();
        }

        // ── Shared Tomestone / FFLogs buttons below all rows ────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

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

        if (cfg.EnableTomestoneIntegration)
        {
            if (string.IsNullOrEmpty(cfg.TomestoneApiKey))
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton(Loc.T("Tomestone API Key Needed##ts_all"));
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(Loc.T("Configure your Tomestone API key in Settings \u2192 Tomestone Integration."));
                }
            }
            else
            {
                // Fixed label; disabled while resolving/loading, or when every member already has data.
                var tsToFetch = TsMembersToFetch(members);
                var tsDisabled = isResolving || tomestoneBatchInProgress || tsToFetch.Count == 0;

                if (tsDisabled)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.SmallButton(Loc.T("Tomestone Lookup##ts_all")) && !tsDisabled)
                {
                    tomestoneBatchInProgress = true;
                    _ = FetchTomestoneForAsync(tsToFetch);
                }

                if (tsDisabled)
                {
                    ImGui.EndDisabled();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    if (isResolving)
                    {
                        ImGui.SetTooltip(Loc.T("Waiting for player names to be resolved\u2026"));
                    }
                    else if (tomestoneBatchInProgress)
                    {
                        ImGui.SetTooltip(Loc.T("Looking up Tomestone data for all players\u2026"));
                    }
                    else if (tsToFetch.Count == 0)
                    {
                        ImGui.SetTooltip(Loc.T("Tomestone data already loaded for this listing"));
                    }
                    else
                    {
                        ImGui.SetTooltip(Loc.T("Look up Tomestone data for all players"));
                    }
                }
            }
        }

        if (cfg.EnableFFLogsIntegrationOverlay)
        {
            if (string.IsNullOrEmpty(cfg.FFLogsClientId) || string.IsNullOrEmpty(cfg.FFLogsClientSecret))
            {
                ImGui.BeginDisabled();
                ImGui.SmallButton(Loc.T("FFLogs API Key Needed##ff_all"));
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(Loc.T("Configure your FFLogs credentials in Settings \u2192 FFLogs Integration."));
                }
            }
            else
            {
                // Fixed label; disabled while resolving/loading, or when every member already has data. Members
                // whose last lookup FAILED are included again so the button re-enables for a manual retry.
                var ffToFetch = FfMembersToFetch(members, includeFailed: true);
                var ffDisabled = isResolving || fflogsBatchInProgress || ffToFetch.Count == 0;
                if (ffDisabled)
                {
                    ImGui.BeginDisabled();
                }

                if (ImGui.SmallButton(Loc.T("FFLogs Lookup##ff_all")) && !ffDisabled)
                {
                    fflogsBatchInProgress = true;
                    _ = FetchFFLogsForAsync(ffToFetch);
                }

                if (ffDisabled)
                {
                    ImGui.EndDisabled();
                }

                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    if (isResolving)
                    {
                        ImGui.SetTooltip(Loc.T("Waiting for player names to be resolved\u2026"));
                    }
                    else if (fflogsBatchInProgress)
                    {
                        ImGui.SetTooltip(Loc.T("Looking up FFLogs data for all players\u2026"));
                    }
                    else if (ffToFetch.Count == 0)
                    {
                        ImGui.SetTooltip(Loc.T("FFLogs data already loaded for this listing"));
                    }
                    else
                    {
                        ImGui.SetTooltip(Loc.T("Look up FFLogs data for all players"));
                    }
                }
            }
        }

        // While a batch is running, cleared/parsed rows appear first and progression fills in a moment later;
        // a footer note makes clear that more data is still coming so the early rows don't read as "done".
        if (fflogsBatchInProgress || tomestoneBatchInProgress)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), Loc.T("Fetching more data…"));
        }

        // Capture this frame's window size for use in PreDraw() clamping next frame
        lastWindowSize = ImGui.GetWindowSize();
    }

    private void DrawMemberRow(PartyMemberInfo member, int index, Configuration cfg, bool hasTomestone, bool hasFFLogs)
    {
        ImGui.TableNextRow();
        ImGui.PushID(index);

        var isBlacklisted = plugin.PartyFinderManager.IsBlacklisted(member.Name, member.World);

        // ── Column 0: Job icon + player name + badges ─────────────────────
        ImGui.TableSetColumnIndex(0);

        // ── Job icon ──────────────────────────────────────────────────────
        var jobIconId = 0u;
        if (!string.IsNullOrWhiteSpace(member.JobAbbreviation))
        {
            var spec = FFLogsService.GetSpecForJob(member.JobAbbreviation);
            var resolvedIconId = FFLogsService.GetJobIconIdForSpec(spec);
            if (resolvedIconId.HasValue)
            {
                jobIconId = resolvedIconId.Value;
            }
        }

        if (cfg.ShowPartyJobIcons && jobIconId > 0)
        {
            try
            {
                var iconLookup = new GameIconLookup(jobIconId);
                var iconHandle = PassportCheckerReborn.TextureProvider.GetFromGameIcon(iconLookup);
                var texture = iconHandle.GetWrapOrDefault();

                if (texture is not null)
                {
                    ImGui.Image(texture.Handle, new Vector2(20, 20));
                    ImGui.SameLine();
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{member.JobAbbreviation,-3}]");
                    ImGui.SameLine();
                }
            }
            catch
            {
                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{member.JobAbbreviation,-3}]");
                ImGui.SameLine();
            }
        }

        // Player label
        var isUnresolved = member.Name.StartsWith(PartyFinderManager.UnresolvedNamePrefix)
            || member.Name.StartsWith(PartyFinderManager.UnresolvedPlayerPrefix);
        var isResolved = !isUnresolved;
        // Anonymized label used for private/unresolved members and, unless the user opts in,
        // for resolved members too. The [Private] badge below distinguishes private members,
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

        if (member.IsPrivate)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), displayName);
        }
        else
        {
            ImGui.TextUnformatted(displayName);
        }

        // Resolved names are clickable (open their FFLogs page) and hover-show provenance + a click hint.
        if (!member.IsPrivate && isResolved)
        {
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                DrawNameProvenanceTooltip(member, showClickHint: true);
            }

            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                OpenFFLogsPageInBrowser(member.Name, member.World);
            }
        }

        if (member.IsPrivate)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), Loc.T("[Private]"));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.T("Adventure plate is hidden or unavailable"));
            }
        }

        if (isBlacklisted)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.9f, 0.2f, 0.2f, 1.0f), "[BL]");
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.T("On your blacklist"));
            }
        }

        // ── PlayerTrack provenance badge (full detail is on the name tooltip) ──
        if (member.FromPlayerTrack)
        {
            ImGui.SameLine();
            ImGui.TextColored(PlayerTrackBadgeColor, "[PT]");
            if (ImGui.IsItemHovered())
            {
                DrawNameProvenanceTooltip(member);
            }
        }

        // ── Column 1: Tomestone data ──────────────────────────────────────
        if (hasTomestone)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate)
            {
                if (tomestoneInfoCache.TryGetValue(member.ContentId, out var cachedTs))
                {
                    if (cachedTs != null)
                    {
                        if (cachedTs.NoLogs)
                        {
                            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), Loc.T("No Logs"));
                        }
                        else
                        {
                            var hasClears = cachedTs.TotalClears.HasValue && cachedTs.TotalClears.Value > 0;
                            var hasProgPoint = !string.IsNullOrWhiteSpace(cachedTs.ProgPoint);
                            var hasBestParse = cachedTs.BestPercent.HasValue;

                            if (hasClears)
                            {
                                var clearsText = "Cleared";
                                if (!string.IsNullOrWhiteSpace(cachedTs.CompletionWeek))
                                {
                                    clearsText += $" ({cachedTs.CompletionWeek})";
                                }

                                if (hasBestParse)
                                {
                                    clearsText += $" | Best: {cachedTs.BestPercent:F1}%";
                                }

                                ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.4f, 1.0f), clearsText);
                            }
                            else if (hasProgPoint)
                            {
                                var progText = cachedTs.ProgPoint!;
                                if (!string.IsNullOrWhiteSpace(cachedTs.DisplayPercent))
                                {
                                    progText += $" ({cachedTs.DisplayPercent})";
                                }

                                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), progText);
                            }
                            else if (hasBestParse)
                            {
                                ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                                    $"Best: {cachedTs.BestPercent:F1}%");
                            }
                            else
                            {
                                ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), Loc.T("Hidden Profile"));
                            }
                        }
                    }
                    else
                    {
                        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), Loc.T("Hidden Profile"));
                    }
                }
                else if (tomestoneBatchInProgress)
                {
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "\u2026");
                }
            }
            else if (member.IsPrivate)
            {
                ImGui.TextColored(new Vector4(0.4f, 0.4f, 0.4f, 1.0f), "-");
            }
        }

        // ── Column 2: FFLogs data ─────────────────────────────────────────
        if (hasFFLogs)
        {
            ImGui.TableNextColumn();
            if (!member.IsPrivate)
            {
                if (fflogsEncounterCache.TryGetValue(member.ContentId, out var cachedFf))
                {
                    DrawFFLogsCellContent(cachedFf, member);
                }
                else if (fflogsBatchInProgress)
                {
                    ImGui.TextColored(NoDataColor, "…");
                }
            }
            else if (member.IsPrivate)
            {
                ImGui.TextColored(NoDataColor, "-");
            }
        }

        ImGui.PopID();
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
    /// Opens one player's FFLogs character page in the browser. The URL is built directly from name + world
    /// (no API lookup), so this spends zero FFLogs rate-limit points.
    /// </summary>
    private static void OpenFFLogsPageInBrowser(string name, string world)
    {
        var url = FFLogsService.GetCharacterPageUrl(name, world);
        if (url is null)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, $"[OverlayWindow] FFLogs page open failed for {name}@{world}");
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


    /// <summary>
    /// Returns an FFLogs-style color for the given parse percentile.
    /// </summary>
    internal static Vector4 GetParseColor(double percentile) => percentile switch
    {
        >= 99 => new Vector4(0.898f, 0.800f, 0.502f, 1.0f),  // Gold (99+)
        >= 95 => new Vector4(0.894f, 0.510f, 0.200f, 1.0f),  // Orange (95-98)
        >= 75 => new Vector4(0.635f, 0.282f, 0.808f, 1.0f),  // Purple (75-94)
        >= 50 => new Vector4(0.118f, 0.392f, 1.000f, 1.0f),  // Blue (50-74)
        >= 25 => new Vector4(0.118f, 0.784f, 0.118f, 1.0f),  // Green (25-49)
        _ => new Vector4(0.600f, 0.600f, 0.600f, 1.0f),  // Grey (<25)
    };

    // Shared FFLogs-cell colours. NoDataColor is slightly dimmer than GetParseColor's <25 grey (0.6) so an
    // absent/no-log cell reads a touch differently from a real-but-low parse, while staying legible.
    internal static readonly Vector4 NoDataColor = new(0.5f, 0.5f, 0.5f, 1.0f);
    private static readonly Vector4 ClearGreen = new(0.4f, 0.8f, 0.4f, 1.0f);
    private static readonly Vector4 ProgYellow = new(0.8f, 0.8f, 0.2f, 1.0f);

    /// <summary>
    /// Renders the FFLogs cell for a member — the single source of truth shared by both overlays. Handles
    /// lookup failure, the unmapped zone-average fallback, multi-phase (P1/P2), single clears, progression,
    /// and no-logs. The caller is responsible for the "still loading" and private-slot cases.
    /// </summary>
    internal static void DrawFFLogsCellContent(EncounterParseResult? cachedFf, PartyMemberInfo member)
    {
        if (cachedFf is null || !cachedFf.HasData)
        {
            if (cachedFf?.FetchFailed == true)
            {
                DrawLookupFailed();
            }
            else
            {
                ImGui.TextColored(NoDataColor, Loc.T("No logs"));
            }

            return;
        }

        if (!cachedFf.IsEncounterSpecific)
        {
            // Unmapped duty: the only signal we have is the character's current-tier average.
            if (cachedFf.BestParse.HasValue)
            {
                DrawParseTemplate(Loc.T("Average overall parse {0}%"), cachedFf.BestParse.Value);
            }
            else
            {
                ImGui.TextColored(NoDataColor, Loc.T("N/A"));
            }

            return;
        }

        var hasMultiPhaseData = cachedFf.Phase1TotalKills.HasValue ||
                                cachedFf.Phase2TotalKills.HasValue ||
                                cachedFf.Phase1BestParse.HasValue ||
                                cachedFf.Phase2BestParse.HasValue ||
                                cachedFf.Phase2LowestBossHpPct.HasValue;

        if (hasMultiPhaseData)
        {
            DrawMultiPhaseCell(cachedFf);
        }
        else if (cachedFf.TotalKills > 0)
        {
            DrawClearedCell(cachedFf, member);
        }
        else if (cachedFf.LowestBossHpPct.HasValue)
        {
            DrawProgression(cachedFf);
        }
        else
        {
            ImGui.TextColored(NoDataColor, Loc.T("No logs"));
        }
    }

    /// <summary>Two-phase (M4-style) fight: each phase's own kills + parse/progress, compact on one line.</summary>
    private static void DrawMultiPhaseCell(EncounterParseResult cachedFf)
    {
        // Both phases on ONE line, compact: "P1 30킬 95.0%   P2 14킬 90.0%". Each phase keeps its own kill
        // count and parse/progression (P1 and P2 usually differ), but the per-job icon + best-on-a-different-
        // job are dropped here — showing them for both phases would make the line too wide.
        DrawPhaseCompact("P1", cachedFf.Phase1Result);
        ImGui.SameLine(0, 14);
        DrawPhaseCompact("P2", cachedFf.Phase2Result);
    }

    /// <summary>One phase of a two-phase fight, compact: "P1 &lt;kills&gt;킬 &lt;parse&gt;%", "P1 &lt;pct&gt;% 전멸", or "P1 기록 없음".</summary>
    private static void DrawPhaseCompact(string label, EncounterParseResult? phase)
    {
        ImGui.TextUnformatted(label);
        ImGui.SameLine();

        if (phase is null || !phase.HasData)
        {
            ImGui.TextColored(NoDataColor, Loc.T("No logs"));
            return;
        }

        if (phase.TotalKills > 0)
        {
            ImGui.TextColored(ClearGreen, string.Format(Loc.T("Cleared {0}X"), phase.TotalKills));
            if (phase.BestParse.HasValue)
            {
                ImGui.SameLine();
                ImGui.TextColored(GetParseColor(phase.BestParse.Value), $"{phase.BestParse.Value:F1}%");
            }
        }
        else if (phase.LowestBossHpPct.HasValue)
        {
            ImGui.TextColored(ProgYellow, string.Format(Loc.T("{0}% wipe"), phase.LowestBossHpPct.Value.ToString("F1")));
        }
        else
        {
            ImGui.TextColored(NoDataColor, Loc.T("No logs"));
        }
    }

    /// <summary>Single-encounter clear: green "Cleared NX" + current-job parse (icon), "-%", or nothing for a non-combat job.</summary>
    private static void DrawClearedCell(EncounterParseResult cachedFf, PartyMemberInfo member)
    {
        var clearedText = string.Format(Loc.T("Cleared {0}X"), cachedFf.TotalKills);

        if (cachedFf.CurrentJobBestParse.HasValue)
        {
            // "46킬 · [current job icon] 1%" — the icon makes clear the % is for the job they're bringing.
            ImGui.TextColored(ClearGreen, clearedText + " ·");
            ImGui.SameLine();
            DrawJobIcon(member.JobAbbreviation,
                FFLogsService.GetJobIconIdForSpec(FFLogsService.GetSpecForJob(member.JobAbbreviation)));
            ImGui.SameLine();
            ImGui.TextColored(GetParseColor(cachedFf.CurrentJobBestParse.Value), $"{cachedFf.CurrentJobBestParse.Value:F1}%");
        }
        else if (FFLogsService.GetSpecForJob(member.JobAbbreviation) is not null)
        {
            // Combat job that cleared but has no ranked parse for this fight: green clear + [icon] -%.
            ImGui.TextColored(ClearGreen, clearedText + " ·");
            ImGui.SameLine();
            DrawJobIcon(member.JobAbbreviation,
                FFLogsService.GetJobIconIdForSpec(FFLogsService.GetSpecForJob(member.JobAbbreviation)));
            ImGui.SameLine();
            ImGui.TextColored(NoDataColor, "-%");
        }
        else
        {
            // Non-combat job (crafter/gatherer): no combat parse to show — just the clear, plus the best
            // parse on a real job (via DrawBestParseOnDifferentJob below).
            ImGui.TextColored(ClearGreen, clearedText);
        }

        DrawBestParseOnDifferentJob(cachedFf, member);
    }

    /// <summary>
    /// Draws a no-kill progression pull in amber, always tagged as a wipe so it can't be mistaken for a parse
    /// percentile. Phased fights read "P3 8.8% 전멸"; phase-less fights read "8.8% 전멸".
    /// </summary>
    internal static void DrawProgression(EncounterParseResult cachedFf)
    {
        if (!cachedFf.LowestBossHpPct.HasValue)
        {
            return;
        }

        var wipe = string.Format(Loc.T("{0}% wipe"), cachedFf.LowestBossHpPct.Value.ToString("F1"));
        var text = cachedFf.ProgLastPhase is int ph && ph >= 2 ? $"P{ph} {wipe}" : wipe;
        ImGui.TextColored(ProgYellow, text);
    }

    /// <summary>Draws an amber "Lookup failed" marker (distinct from grey "No logs") with a retry hint tooltip.</summary>
    internal static void DrawLookupFailed()
    {
        ImGui.TextColored(new Vector4(0.85f, 0.55f, 0.25f, 1.0f), Loc.T("Lookup failed"));
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("FFLogs lookup failed (network or rate limit) — refresh to retry."));
        }
    }

    /// <summary>
    /// Renders a localized "…{0}%" parse template with only the percentage (and its trailing "%") tinted by
    /// parse grade; the surrounding descriptive label stays neutral grey. Falls back to a fully-coloured
    /// string if the template has no <c>{0}</c> placeholder.
    /// </summary>
    internal static void DrawParseTemplate(string template, double pct)
    {
        var placeholder = template.IndexOf("{0}", StringComparison.Ordinal);
        var pctText = pct.ToString("F1");

        if (placeholder < 0)
        {
            ImGui.TextColored(GetParseColor(pct), string.Format(template, pctText));
            return;
        }

        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), template[..placeholder]);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(GetParseColor(pct), pctText + template[(placeholder + 3)..]);
    }

    /// <summary>
    /// If the overall best parse is on a different job from the member's current job,
    /// draws it on a new line with the job icon. Does nothing when the current job
    /// IS the best job (no redundant display).
    /// </summary>
    private static void DrawBestParseOnDifferentJob(EncounterParseResult cachedFf, PartyMemberInfo member)
    {
        if (!cachedFf.BestParse.HasValue || cachedFf.BestParseJobAbbreviation == null)
        {
            return;
        }

        // If current job is the best job, only current job parse is shown – skip
        if (string.Equals(cachedFf.BestParseJobAbbreviation, member.JobAbbreviation,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // If the current job parse already matches or exceeds the best, skip
        if (cachedFf.CurrentJobBestParse.HasValue &&
            cachedFf.BestParse.Value <= cachedFf.CurrentJobBestParse.Value)
        {
            return;
        }

        DrawJobSpecBestParse(cachedFf.BestParse.Value, cachedFf.BestParseJobAbbreviation,
            cachedFf.BestParseJobIconId);
    }

    /// <summary>
    /// Draws a "Best on [icon] JOB: X%" line showing a parse for a specific job.
    /// </summary>
    internal static void DrawJobSpecBestParse(double parse, string jobAbbreviation, uint? jobIconId)
    {
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), "·");
        ImGui.SameLine();
        DrawJobIcon(jobAbbreviation, jobIconId);
        ImGui.SameLine();
        ImGui.TextColored(GetParseColor(parse), $"{parse:F1}%");
    }

    /// <summary>Draws a job icon inline (16px), or a "[ABBR]" text fallback. Caller handles SameLine.</summary>
    private static void DrawJobIcon(string jobAbbreviation, uint? iconId, float size = 16f)
    {
        if (iconId.HasValue)
        {
            try
            {
                var lookup = new GameIconLookup(iconId.Value);
                var texture = PassportCheckerReborn.TextureProvider.GetFromGameIcon(lookup).GetWrapOrDefault();
                if (texture is not null)
                {
                    ImGui.Image(texture.Handle, new Vector2(size, size));
                    return;
                }
            }
            catch
            {
                // fall through to the text fallback
            }
        }

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f), $"[{jobAbbreviation}]");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PlayerTrack helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly Vector4 PlayerTrackBadgeColor = new(0.4f, 0.8f, 0.9f, 1.0f);

    /// <summary>
    /// Tooltip shown when hovering a resolved member's name (or the [PT] badge): the name's source,
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
                    ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), Loc.T("This name is old and may be out of date."));
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

            ImGui.TextColored(new Vector4(0.6f, 0.6f, 0.6f, 1.0f), Loc.T("Click to open FFLogs page"));
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
