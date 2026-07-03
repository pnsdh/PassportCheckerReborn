using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using System;
using System.Diagnostics;
using System.Numerics;

namespace PassportCheckerReborn.Windows;

public class MainWindow : Window, IDisposable
{
    private readonly PassportCheckerReborn plugin;
    private Configuration Configuration => plugin.Configuration;

    // Temporary buffers for FFLogs credential input
    private string fFlogsClientIdInput = string.Empty;
    private string fFlogsClientSecretInput = string.Empty;
    private string fFlogsTestResult = string.Empty;
    private bool fFlogsTestInProgress = false;

    // FFLogs API usage display
    private string fFlogsUsageText = string.Empty;
    private bool fFlogsUsageInProgress;
    private bool fFlogsUsageRequested;

    // Temporary buffer for Tomestone API key input
    private string tomestoneApiKeyInput = string.Empty;

    public MainWindow(PassportCheckerReborn plugin)
        : base("Passport Checker Reborn (Custom) – Settings###PassportCheckerRebornSettings",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 260),
            MaximumSize = new Vector2(600, 600)
        };

        TitleBarButtons.Add(new TitleBarButton()
        {
            Icon = FontAwesomeIcon.MugHot,
            ShowTooltip = () =>
            {
                ImGui.BeginTooltip();
                ImGui.Text("Support the developer on Ko-fi");
                ImGui.EndTooltip();
            },
            Priority = 2,
            Click = _ =>
            {
                try
                {
                    Util.OpenLink("https://ko-fi.com/ltscombatreborn");
                }
                catch
                {
                    // ignored
                }
            },
            AvailableClickthrough = true
        });

        // Populate credential buffers from saved config
        fFlogsClientIdInput = Configuration.FFLogsClientId;
        fFlogsClientSecretInput = Configuration.FFLogsClientSecret;
        tomestoneApiKeyInput = Configuration.TomestoneApiKey;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override void Draw()
    {
        DrawLanguageSelector();

        if (!ImGui.BeginTabBar("##SettingsTabs"))
        {
            return;
        }

        DrawGeneralTab();
        DrawOverlayTab();
        // Always show these tabs (they hold API config that's useful before enabling); the tab used to
        // appear/disappear with the toggle, which was confusing. Each shows an "off" hint when disabled.
        // Tomestone is unsupported on the Korean client, so its tab stays hidden there.
        DrawFFLogsTab();
        if (!PassportCheckerReborn.IsKoreanClient)
        {
            DrawTomestoneTab();
        }
        DrawPlayerTrackTab();
        DrawAboutTab();

        ImGui.EndTabBar();
    }

    private void DrawLanguageSelector()
    {
        var names = Enum.GetNames(typeof(PluginLanguage));
        var display = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
        {
            display[i] = Loc.T(names[i]);
        }

        var current = (int)Configuration.Language;
        ImGui.TextUnformatted(Loc.T("Language"));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(140f);
        if (ImGui.Combo("##pcr_language", ref current, display, display.Length))
        {
            Configuration.Language = (PluginLanguage)current;
            Loc.Language = Configuration.Language;
            Configuration.Save();
        }

        ImGui.Separator();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // General Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawGeneralTab()
    {
        if (!ImGui.BeginTabItem(Loc.T("General")))
        {
            return;
        }

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("Party Finder Detail Optimizations"));
        ImGui.Separator();
        ImGui.Spacing();

        var keepOpen = Configuration.PreventAutoClosingOnPartyChanges2;
        if (ImGui.Checkbox(Loc.T("Keep the Party Finder window open when the party changes"), ref keepOpen))
        {
            Configuration.PreventAutoClosingOnPartyChanges2 = keepOpen;
            Configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("The game normally closes the Party Finder detail window when your party composition changes; this keeps it (and the member overlay) open."));
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("Party Finder List Optimizations"));
        ImGui.Separator();
        ImGui.Spacing();

        var autoRefresh = Configuration.EnableAutomaticRefresh;
        if (ImGui.Checkbox(Loc.T("Enable Automatic Refresh for Party Finder Listings"), ref autoRefresh))
        {
            Configuration.EnableAutomaticRefresh = autoRefresh;
            Configuration.Save();
        }

        if (Configuration.EnableAutomaticRefresh)
        {
            ImGui.Indent(20f);
            var interval = Configuration.AutoRefreshIntervalSeconds;
            if (ImGui.SliderInt(Loc.T("Refresh Interval (seconds)##refresh"), ref interval, 10, 120))
            {
                Configuration.AutoRefreshIntervalSeconds = interval;
                Configuration.Save();
            }
            ImGui.Unindent(20f);
        }

        var rightClick = Configuration.RightClickPlayerNameForRecruitment3;
        if (ImGui.Checkbox(Loc.T("Right-Click Player Name to View Their Recruitment"), ref rightClick))
        {
            Configuration.RightClickPlayerNameForRecruitment3 = rightClick;
            Configuration.Save();

            // Register/unregister the context-menu entry immediately so the toggle takes effect now.
            if (rightClick)
            {
                plugin.PartyFinderManager.RegisterContextMenu();
            }
            else
            {
                plugin.PartyFinderManager.UnregisterContextMenu();
            }
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("Adds a 'View Recruitment' option when you right-click a player. If they're hosting a Party Finder listing, it finds and opens it."));
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("Blacklist"));
        ImGui.Separator();
        ImGui.Spacing();

        var enableBlacklist = Configuration.EnableBlacklistFeature;
        if (ImGui.Checkbox(Loc.T("Enable Blacklist Feature"), ref enableBlacklist))
        {
            Configuration.EnableBlacklistFeature = enableBlacklist;
            Configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("When enabled, players on your in-game blacklist are marked with [BL] in the overlay."));
        }

        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Refresh##bl_refresh")))
        {
            plugin.PartyFinderManager.ForceRefreshBlacklist();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("Re-reads the blacklist from the game and saves the result."));
        }

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Overlay Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawOverlayTab()
    {
        if (!ImGui.BeginTabItem(Loc.T("Overlay")))
        {
            return;
        }

        ImGui.Spacing();

        var showOverlay = Configuration.ShowMemberInfoOverlay;
        if (ImGui.Checkbox(Loc.T("Show Member Info Overlay in PF Details"), ref showOverlay))
        {
            Configuration.ShowMemberInfoOverlay = showOverlay;
            Configuration.Save();
        }

        ImGui.Spacing();

        // These options only affect the member overlay, so grey them out when it's off. The FFLogs/Tomestone
        // toggles and the Party List overlay below are independent (the party list uses them too), so they
        // stay enabled regardless.
        using (new ImGuiDisabledScope(!Configuration.ShowMemberInfoOverlay))
        {
            var highEndOnly = Configuration.OnlyShowOverlayForHighEndDuties;
            if (ImGui.Checkbox(Loc.T("Only Show Overlay for High-End Duties"), ref highEndOnly))
            {
                Configuration.OnlyShowOverlayForHighEndDuties = highEndOnly;
                Configuration.Save();
            }

            var leftSide = Configuration.ShowOverlayOnLeftSide;
            if (ImGui.Checkbox(Loc.T("Show overlay on the left side"), ref leftSide))
            {
                Configuration.ShowOverlayOnLeftSide = leftSide;
                Configuration.Save();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.T("Unchecked places the overlay on the right side of the Party Finder."));
            }

            var showJobIcons = Configuration.ShowPartyJobIcons;
            if (ImGui.Checkbox(Loc.T("Show Party Job Icons"), ref showJobIcons))
            {
                Configuration.ShowPartyJobIcons = showJobIcons;
                Configuration.Save();
            }

            var showResolvedNames = Configuration.ShowResolvedPlayerNames;
            if (ImGui.Checkbox(Loc.T("Show Resolved Player Names in Member Info Overlay"), ref showResolvedNames))
            {
                Configuration.ShowResolvedPlayerNames = showResolvedNames;
                Configuration.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Loc.T("When enabled, displays the actual player name (Name@World) once resolved, instead of \"Player X\"."));
            }
        }

        // Name freshness — re-verify stale cached names against the adventure plate. Independent of the
        // member overlay (it maintains the shared name cache), so it stays enabled regardless.
        var reverify = Configuration.EnableStaleNameReverification;
        if (ImGui.Checkbox(Loc.T("Re-verify stale player names via adventure plate"), ref reverify))
        {
            Configuration.EnableStaleNameReverification = reverify;
            Configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("When a cached name is older than the threshold below, quietly re-checks it against the player's adventure plate. Throttled by the cooldown so the same stale name isn't re-checked constantly; detected renames are recorded in the name history."));
        }

        if (Configuration.EnableStaleNameReverification)
        {
            ImGui.Indent(20f);
            var staleDays = Configuration.StaleNameThresholdDays;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.InputInt(Loc.T("Stale after (days)##stale_days"), ref staleDays, 1, 5))
            {
                Configuration.StaleNameThresholdDays = Math.Clamp(staleDays, 1, 3650);
                Configuration.Save();
            }

            var cooldownHours = Configuration.ReverifyCooldownHours;
            ImGui.SetNextItemWidth(160f);
            if (ImGui.InputInt(Loc.T("Retry cooldown (hours)##reverify_cd"), ref cooldownHours, 1, 12))
            {
                Configuration.ReverifyCooldownHours = Math.Clamp(cooldownHours, 1, 8760);
                Configuration.Save();
            }
            ImGui.Unindent(20f);
        }

        var privateCd = Configuration.PrivatePlayerReverifyCooldownHours;
        ImGui.SetNextItemWidth(160f);
        if (ImGui.InputInt(Loc.T("Re-check hidden (Private) players every (hours)##private_cd"), ref privateCd, 1, 6))
        {
            Configuration.PrivatePlayerReverifyCooldownHours = Math.Clamp(privateCd, 1, 8760);
            Configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("How often to re-attempt an adventure-plate lookup for players whose plate is hidden. Higher = fewer wasted requests, but slower to notice if they make their plate public."));
        }

        var fflogsOverlay = Configuration.EnableFFLogsIntegrationOverlay;
        if (ImGui.Checkbox(
                Loc.T("Enable FFLogs Integration (configure in FFLogs Integration tab)"),
                ref fflogsOverlay))
        {
            Configuration.EnableFFLogsIntegrationOverlay = fflogsOverlay;
            Configuration.Save();
        }

        // Tomestone.gg has no Korean data — disable the toggle entirely on the KR client.
        using (new ImGuiDisabledScope(PassportCheckerReborn.IsKoreanClient))
        {
            var tomestone = Configuration.EnableTomestoneIntegration;
            if (ImGui.Checkbox(
                    Loc.T("Enable Tomestone Integration (configure API key in Tomestone Integration tab)"),
                    ref tomestone))
            {
                Configuration.EnableTomestoneIntegration = tomestone;
                Configuration.Save();
            }
        }
        if (PassportCheckerReborn.IsKoreanClient && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(Loc.T("Tomestone.gg has no data for the Korean data centres, so it's unavailable on the Korean client."));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("Party List Overlay"));
        ImGui.Spacing();

        var partyListOverlay = Configuration.ShowPartyListOverlay;
        if (ImGui.Checkbox(Loc.T("Show Info for Current Party Members"), ref partyListOverlay))
        {
            Configuration.ShowPartyListOverlay = partyListOverlay;
            Configuration.Save();
        }

        if (Configuration.ShowPartyListOverlay)
        {
            ImGui.Indent(20f);
            ImGui.TextWrapped(Loc.T(
                "Shows FFLogs and Tomestone data for your current party members in an overlay " +
                "attached to the Party Members UI element. " +
                "Requires FFLogs Integration and/or Tomestone Integration to be enabled. " +
                "Includes a duty selector dropdown for encounter-specific lookups."));

            var positionNames = Enum.GetNames(typeof(PartyListOverlayPosition));
            var currentPosition = (int)Configuration.PartyListOverlayPosition;
            ImGui.TextUnformatted(Loc.T("Overlay Position:"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(150f);
            if (ImGui.Combo("##party_list_position", ref currentPosition, positionNames, positionNames.Length))
            {
                Configuration.PartyListOverlayPosition = (PartyListOverlayPosition)currentPosition;
                Configuration.Save();
            }

            var hideInDuty = Configuration.HidePartyListInDuty;
            if (ImGui.Checkbox(Loc.T("Hide Party List Overlay while in a duty"), ref hideInDuty))
            {
                Configuration.HidePartyListInDuty = hideInDuty;
                Configuration.Save();
            }

            var hideInCombat = Configuration.HidePartyListInCombat;
            if (ImGui.Checkbox(Loc.T("Hide Party List Overlay while in combat"), ref hideInCombat))
            {
                Configuration.HidePartyListInCombat = hideInCombat;
                Configuration.Save();
            }

            ImGui.Unindent(20f);
        }

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FFLogs Integration Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawFFLogsTab()
    {
        if (!ImGui.BeginTabItem(Loc.T("FFLogs Integration")))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("FFLogs API Configuration"));
        ImGui.Separator();
        ImGui.Spacing();

        if (!Configuration.EnableFFLogsIntegrationOverlay)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                Loc.T("This integration is off. Turn it on in the Overlay tab to show FFLogs in the overlay."));
            ImGui.Spacing();
        }

        ImGui.TextUnformatted(Loc.T("Client ID"));
        ImGui.SetNextItemWidth(360f);
        if (ImGui.InputText("##fflogs_client_id", ref fFlogsClientIdInput, 128))
        {
            // don't save on every keystroke – save on button press
        }

        ImGui.Spacing();

        ImGui.TextUnformatted(Loc.T("Client Secret"));
        ImGui.SetNextItemWidth(360f);
        if (ImGui.InputText("##fflogs_client_secret", ref fFlogsClientSecretInput, 128,
                            ImGuiInputTextFlags.Password))
        {
            // don't save on every keystroke – save on button press
        }

        ImGui.Spacing();

        // Capture state before the button so BeginDisabled/EndDisabled are always balanced.
        var wasTestInProgress = fFlogsTestInProgress;
        if (wasTestInProgress)
        {
            ImGui.BeginDisabled();
        }

        if (ImGui.Button(Loc.T("Save & Test Credentials")))
        {
            Configuration.FFLogsClientId = fFlogsClientIdInput;
            Configuration.FFLogsClientSecret = fFlogsClientSecretInput;
            Configuration.Save();
            fFlogsTestResult = string.Empty;
            fFlogsTestInProgress = true;
            fFlogsUsageRequested = false;  // re-fetch usage with the new credentials

            _ = TestFFLogsCredentialsAsync().ContinueWith(
                t => PassportCheckerReborn.Log.Warning(t.Exception, "[PassportCheckerReborn] Unhandled error in credential test."),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
        }

        if (wasTestInProgress)
        {
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextUnformatted(Loc.T("Testing…"));
        }

        if (!string.IsNullOrEmpty(fFlogsTestResult))
        {
            ImGui.Spacing();
            var success = fFlogsTestResult.StartsWith("OK");
            ImGui.TextColored(
                success ? new Vector4(0.2f, 0.8f, 0.2f, 1.0f) : new Vector4(0.9f, 0.2f, 0.2f, 1.0f),
                fFlogsTestResult);
        }

        ImGui.Spacing();
        var autoFetch = Configuration.AutoFetchFFLogsWhenResolved;
        if (ImGui.Checkbox(Loc.T("Automatically look up FFLogs data once all names are resolved"), ref autoFetch))
        {
            Configuration.AutoFetchFFLogsWhenResolved = autoFetch;
            Configuration.Save();
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("Runs a lookup automatically when every name resolves. Spends FFLogs API points (see below)."));
        }

        // ── API usage (shown when credentials are configured) ──
        if (!string.IsNullOrWhiteSpace(Configuration.FFLogsClientId) && !string.IsNullOrWhiteSpace(Configuration.FFLogsClientSecret))
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("API Usage"));
            ImGui.Separator();
            ImGui.Spacing();

            // Prefer the counters piggybacked onto recent FFLogs traffic; only spend a dedicated request to
            // seed the display when no query has carried them yet.
            var usage = plugin.FFLogsService.GetCachedRateLimit();
            if (usage is { } u)
            {
                ImGui.TextUnformatted(FormatFFLogsUsage(u.PointsSpentThisHour, u.LimitPerHour, u.PointsResetInSeconds));
            }
            else if (fFlogsUsageInProgress)
            {
                ImGui.TextUnformatted(Loc.T("Checking…"));
            }
            else
            {
                ImGui.TextUnformatted(fFlogsUsageText);
            }

            if (usage is null && !fFlogsUsageRequested && !fFlogsUsageInProgress)
            {
                fFlogsUsageRequested = true;
                fFlogsUsageInProgress = true;
                _ = RefreshFFLogsUsageAsync();
            }

            ImGui.SameLine();
            if (ImGui.SmallButton(Loc.T("Refresh##fflogs_usage")))
            {
                fFlogsUsageInProgress = true;
                _ = RefreshFFLogsUsageAsync();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Collapsible "How to obtain credentials" guide
        if (ImGui.CollapsingHeader(Loc.T("How to obtain FFLogs API credentials")))
        {
            const string fflogsApiUrl = "https://www.fflogs.com/api/clients/";
            const string exampleClientName = "PassportCheckerReborn";
            const string exampleRedirectUrl = "https://example.com/";

            ImGui.Spacing();
            ImGui.TextUnformatted(Loc.T("1. Navigate to FFLogs API portal:"));
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Open FFLogs API Portal")))
            {
                OpenUrl(fflogsApiUrl);
            }

            ImGui.Spacing();
            ImGui.TextUnformatted(Loc.T("2. Click 'Create Client' in the top-right corner."));
            ImGui.Spacing();

            ImGui.TextUnformatted(Loc.T("3. Enter a client name (e.g. 'PassportCheckerReborn')."));
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Copy Client Name")))
            {
                ImGui.SetClipboardText(exampleClientName);
            }

            ImGui.Spacing();
            ImGui.TextUnformatted(Loc.T("4. Provide any Redirect URL (e.g. 'https://example.com/')."));
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Copy Redirect URL")))
            {
                ImGui.SetClipboardText(exampleRedirectUrl);
            }

            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("5. Leave 'Public Client' unchecked. "));
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("6. Copy the generated Client ID/Secret to the fields above. "));
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("7. Click 'Save & Test Credentials' to verify token status. "));
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("Note: The Client Secret is only shown once. Keep it private."));
        }

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tomestone Integration Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawTomestoneTab()
    {
        if (!ImGui.BeginTabItem(Loc.T("Tomestone Integration")))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("Tomestone API Configuration"));
        ImGui.Separator();
        ImGui.Spacing();

        if (!Configuration.EnableTomestoneIntegration)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.8f, 0.2f, 1.0f),
                Loc.T("This integration is off. Turn it on in the Overlay tab to show Tomestone in the overlay."));
            ImGui.Spacing();
        }

        ImGui.TextWrapped(Loc.T(
            "When enabled, clicking 'Tomestone' in the overlay will fetch prog point and " +
            "activity data for the current duty from the Tomestone.gg API."));
        ImGui.Spacing();

        ImGui.TextUnformatted(Loc.T("API Key (Bearer token)"));
        ImGui.SetNextItemWidth(360f);
        if (ImGui.InputText("##tomestone_api_key", ref tomestoneApiKeyInput, 256,
                            ImGuiInputTextFlags.Password))
        {
            // Saved when the user clicks the Save button below
        }

        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Save##ts_save")))
        {
            Configuration.TomestoneApiKey = tomestoneApiKeyInput;
            Configuration.Save();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Collapsible "How to obtain your API key" guide
        if (ImGui.CollapsingHeader(Loc.T("How to obtain a Tomestone API key")))
        {
            const string tomestoneAccountUrl = "https://tomestone.gg/profile/account";

            ImGui.Spacing();
            ImGui.TextUnformatted(Loc.T("1. Navigate to Tomestone Account Settings:"));
            ImGui.SameLine();
            if (ImGui.Button(Loc.T("Open Tomestone Account Page")))
            {
                OpenUrl(tomestoneAccountUrl);
            }

            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("2. Scroll down to the \"API access token\" section."));
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("3. Click \"Generate access token\"."));
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("4. Copy the generated token and paste it into the field above."));
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("5. Click 'Save' to store your API key."));
            ImGui.Spacing();
            ImGui.TextWrapped(Loc.T("Note: Keep your API token private. It grants access to your Tomestone account data."));
        }

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PlayerTrack Integration Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawPlayerTrackTab()
    {
        if (!ImGui.BeginTabItem(Loc.T("PlayerTrack")))
        {
            return;
        }

        ImGui.Spacing();
        ImGui.TextColored(new Vector4(1.0f, 0.85f, 0.0f, 1.0f), Loc.T("PlayerTrack Integration"));
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(Loc.T(
            "When enabled, party members whose name can't be read from Party Finder packets or the " +
            "adventure plate are looked up in the PlayerTrack plugin's local database (read-only). " +
            "This can recover names of players who hide their adventure plate, as long as you have " +
            "encountered them before."));
        ImGui.Spacing();

        var svc = plugin.PlayerTrackService;
        var (installed, loaded) = svc.GetPluginStatus();
        var dbExists = svc.DatabaseExists;

        var green = new Vector4(0.2f, 0.8f, 0.2f, 1.0f);
        var red = new Vector4(0.9f, 0.3f, 0.3f, 1.0f);
        var grey = new Vector4(0.6f, 0.6f, 0.6f, 1.0f);

        ImGui.TextUnformatted(Loc.T("Status:"));
        ImGui.Indent(12f);
        ImGui.TextColored(installed ? green : red, installed ? Loc.T("PlayerTrack plugin installed") : Loc.T("PlayerTrack plugin not found"));
        ImGui.TextColored(loaded ? green : grey, loaded ? Loc.T("PlayerTrack is loaded") : Loc.T("PlayerTrack not currently loaded"));
        ImGui.TextColored(dbExists ? green : red, dbExists ? Loc.T("Database found") : Loc.T("Database not found"));
        if (dbExists)
        {
            ImGui.TextColored(grey, svc.DatabasePath);
        }
        ImGui.Unindent(12f);

        if (!dbExists)
        {
            ImGui.Spacing();
            ImGui.TextColored(red, Loc.T("Integration is inactive: the PlayerTrack database was not found."));
            ImGui.TextWrapped(Loc.T("Install and run PlayerTrack at least once so it builds its database, then reopen this window."));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var enabled = Configuration.EnablePlayerTrackIntegration;
        if (ImGui.Checkbox(Loc.T("Enable PlayerTrack name resolution"), ref enabled))
        {
            Configuration.EnablePlayerTrackIntegration = enabled;
            Configuration.Save();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("Reads PlayerTrack's database (read-only) to resolve otherwise-unknown party member names."));
        }

        using (new ImGuiDisabledScope(!Configuration.EnablePlayerTrackIntegration))
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(Loc.T("Resolution priority:"));

            var options = new[]
            {
                Loc.T("Adventure Plate first (freshest)"),
                Loc.T("PlayerTrack first (fastest)"),
            };
            var current = (int)Configuration.PlayerTrackPriority;
            ImGui.SetNextItemWidth(280f);
            if (ImGui.Combo("##pt_priority", ref current, options, options.Length))
            {
                Configuration.PlayerTrackPriority = (PlayerTrackResolutionPriority)current;
                Configuration.Save();
            }

            ImGui.Indent(12f);
            if (Configuration.PlayerTrackPriority == PlayerTrackResolutionPriority.CharaCardFirst)
            {
                ImGui.TextWrapped(Loc.T(
                    "Tries the live adventure plate first (most up-to-date name). " +
                    "If the plate is hidden or the lookup fails, falls back to PlayerTrack."));
            }
            else
            {
                ImGui.TextWrapped(Loc.T(
                    "Uses PlayerTrack's stored name first (instant, no network request, works for hidden plates). " +
                    "Only queries the adventure plate when PlayerTrack has no record. " +
                    "Note: PlayerTrack data can be stale if the player has since renamed or transferred worlds."));
            }
            ImGui.Unindent(12f);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextWrapped(Loc.T(
            "Names resolved via PlayerTrack are marked with a [PT] tag in the overlay. Hover a member's " +
            "name (or the tag) to see the name's source, how old the cached data is, and any previous names."));

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // About Tab
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawAboutTab()
    {
        if (!ImGui.BeginTabItem(Loc.T("About")))
        {
            return;
        }

        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), Loc.T("Passport Checker Reborn") + " (Custom)");
        ImGui.TextUnformatted(Loc.T("An open-source Party Finder enhancement plugin for Final Fantasy XIV."));
        ImGui.Spacing();

        ImGui.TextUnformatted(Loc.T("Author:  The Combat Reborn Team - LTS"));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(Loc.T(
            "Passport Checker Reborn is an open-source alternative to the PFFinder plugin. " +
            "It shows a member-info overlay alongside party finder listings, integrates with " +
            "Tomestone.gg and FFLogs for quick prog-point lookups, and offers quality-of-life improvements " +
            "to the party finder UI."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextWrapped(Loc.T(
            "Commands:\n" +
            "  /pfchecker (or /pcr)  – Open the settings window.\n" +
            "  /pcrparty  – Toggle the party list overlay window.\n"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), Loc.T("Overlay markers"));
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 0.9f, 1.0f), "[PT]");
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("Name recovered from the PlayerTrack database"));

        ImGui.TextColored(new Vector4(0.9f, 0.2f, 0.2f, 1.0f), "[BL]");
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("On your in-game blacklist"));

        ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1.0f), Loc.T("[Private]"));
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("Adventure plate is hidden"));

        ImGui.TextColored(new Vector4(0.85f, 0.55f, 0.25f, 1.0f), Loc.T("Lookup failed"));
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("FFLogs request failed — refresh to retry"));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), Loc.T("Cache Statistics"));
        ImGui.Spacing();
        ImGui.TextUnformatted($"{Loc.T("Resolved CIDs")}:  {plugin.CidCache.Count}");
        ImGui.SameLine();
        // Resolved names are kept indefinitely by design, so guard the wipe behind SHIFT + a confirmation
        // dialog to make an accidental click impossible.
        var cidShiftHeld = ImGui.GetIO().KeyShift;
        using (new ImGuiDisabledScope(!cidShiftHeld))
        {
            if (ImGui.SmallButton(Loc.T("Clear Cache##cid_clear")))
            {
                ImGui.OpenPopup(CidClearPopupId);
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip(Loc.T(
                "Deletes all stored Content ID → name/world mappings and their name history from disk. " +
                "Names are re-learned as you encounter players again.\n" +
                "Hold SHIFT and click to enable this button."));
        }

        ImGui.TextUnformatted($"{Loc.T("Blacklisted players")}:  {plugin.BlacklistCache.Count}");
        ImGui.SameLine();
        if (ImGui.SmallButton(Loc.T("Clear Cache##bl_clear")))
        {
            plugin.BlacklistCache.Clear();
            plugin.PartyFinderManager.ForceRefreshBlacklist();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Loc.T("Clears the persisted blacklist cache, then re-reads from the game."));
        }

        DrawCidClearConfirmPopup();

        ImGui.Spacing();
        ImGui.EndTabItem();
    }

    private const string CidClearPopupId = "###cid_clear_confirm";

    /// <summary>Confirmation modal for wiping the resolved-name (CID) cache. Opened from the About tab.</summary>
    private void DrawCidClearConfirmPopup()
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(Loc.T("Clear cached names?") + CidClearPopupId,
                ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextUnformatted(string.Format(
            Loc.T("This permanently deletes all {0} stored names and their history. This cannot be undone."),
            plugin.CidCache.Count));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if (ImGui.Button(Loc.T("Delete"), new Vector2(120, 0)))
        {
            plugin.CidCache.Clear();
            ImGui.CloseCurrentPopup();
        }
        ImGui.SameLine();
        if (ImGui.Button(Loc.T("Cancel"), new Vector2(120, 0)))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────
    private async System.Threading.Tasks.Task TestFFLogsCredentialsAsync()
    {
        try
        {
            var result = await plugin.FFLogsService.TestCredentialsAsync(
                Configuration.FFLogsClientId,
                Configuration.FFLogsClientSecret);

            fFlogsTestResult = result ? "OK – Credentials accepted by FFLogs!" : "ERROR – Invalid credentials.";
        }
        catch (Exception ex)
        {
            fFlogsTestResult = $"ERROR – {ex.Message}";
            PassportCheckerReborn.Log.Warning(ex, "[PassportCheckerReborn] FFLogs credential test failed.");
        }
        finally
        {
            fFlogsTestInProgress = false;
        }
    }

    private static string FormatFFLogsUsage(double spent, int limit, int resetSeconds)
        => string.Format(Loc.T("{0} / {1} points used this hour (resets in {2} min)"),
            spent.ToString("F0"), limit, Math.Max(0, resetSeconds / 60));

    private async System.Threading.Tasks.Task RefreshFFLogsUsageAsync()
    {
        try
        {
            var rl = await plugin.FFLogsService.GetRateLimitAsync();
            fFlogsUsageText = rl is { } r
                ? FormatFFLogsUsage(r.PointsSpentThisHour, r.LimitPerHour, r.PointsResetInSeconds)
                : Loc.T("Could not retrieve API usage.");
        }
        catch (Exception ex)
        {
            fFlogsUsageText = Loc.T("Could not retrieve API usage.");
            PassportCheckerReborn.Log.Warning(ex, "[PassportCheckerReborn] FFLogs usage check failed.");
        }
        finally
        {
            fFlogsUsageInProgress = false;
        }
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, $"[PassportCheckerReborn] Failed to open URL {url}");
        }
    }

    // Minimal scope wrapper so we can use `using var` without bringing in Raii
    private sealed class ImGuiDisabledScope : IDisposable
    {
        public ImGuiDisabledScope(bool disabled) { if (disabled) { ImGui.BeginDisabled(); } this.disabled = disabled; }
        private readonly bool disabled;
        public void Dispose()
        {
            if (disabled)
            {
                ImGui.EndDisabled();
            }
        }
    }
}
