using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;
using PassportCheckerReborn.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Windows;

public class MainWindow : Window, IDisposable
{
    private enum Page
    {
        General,
        Overlays,
        FFLogs,
        Tomestone,
        PlayerTrack,
        Appearance,
        About,
    }

    private const string KofiUrl = "https://ko-fi.com/ltscombatreborn";
    private const string RepoUrl = "https://github.com/pnsdh/PassportCheckerReborn";
    private const string DiscordUrl = "https://discord.gg/AjVHyDNjUd";
    private const string FFLogsClientsUrl = "https://www.fflogs.com/api/clients/";
    private const string FFLogsExampleClientName = "PassportCheckerReborn";
    private const string FFLogsExampleRedirectUrl = "https://example.com/";
    private const string TomestoneAccountUrl = "https://tomestone.gg/profile/account";
    private const string CidClearPopupId = "###cid_clear_confirm";

    /// <summary>Content width (unscaled) below which the navigation drawer collapses to an icon rail.</summary>
    private const float DrawerLayoutWidth = 620f;
    private const float DrawerWidth = 188f;
    private const float RailWidth = 84f;

    /// <summary>How long a guide's copy button reads "Copied" after a click, in seconds.</summary>
    private const double CopiedFeedbackSeconds = 1.5;

    private static readonly string[] OverlayPositionNames = Enum.GetNames<PartyListOverlayPosition>();

    /// <summary>The unit suffixes the settings sliders use; the widest sets the shared readout gutter.</summary>
    private static readonly string[] SliderUnits = [" s", " days", " h"];

    /// <summary>The text size steps offered on the Appearance page.</summary>
    private static readonly float[] TextScaleSteps = [1.0f, 1.1f, 1.2f, 1.35f];

    private static readonly (string Name, Vector4 Color)[] AccentPresets =
    [
        ("Crimson (default)", M3.DefaultSeed),
        ("Sapphire", M3ColorMath.FromRgb(0x2F6DB5)),
        ("Teal", M3ColorMath.FromRgb(0x00897B)),
        ("Forest", M3ColorMath.FromRgb(0x3E8E41)),
        ("Amber", M3ColorMath.FromRgb(0xC98A00)),
        ("Violet", M3ColorMath.FromRgb(0x7E57C2)),
        ("Rose", M3ColorMath.FromRgb(0xC2185B)),
    ];

    private readonly PassportCheckerReborn plugin;
    private readonly string version = typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "?";

    private M3Style.Scope? theme;
    private Page page = Page.General;

    // Sliders and colour pickers change the config every frame while dragged, so their writes to
    // disk wait until the control is let go.
    private bool savePending;

    private string fflogsClientIdInput;
    private string fflogsClientSecretInput;
    private string fflogsTestMessage = string.Empty;
    private bool fflogsTestSucceeded;
    private bool fflogsTestInProgress;
    private bool fflogsGuideExpanded;
    private string tomestoneApiKeyInput;
    private bool tomestoneGuideExpanded;

    // FFLogs API usage display
    private string fflogsUsageText = string.Empty;
    private bool fflogsUsageInProgress;
    private bool fflogsUsageRequested;

    private string? copiedId;
    private double copiedAt;

    public MainWindow(PassportCheckerReborn plugin)
        : base("Passport Checker Reborn (Custom) – Settings###PassportCheckerRebornSettings",
               ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        this.plugin = plugin;

        Size = new Vector2(820, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(440, 360),
            MaximumSize = new Vector2(1600, 1400)
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
                    Util.OpenLink(KofiUrl);
                }
                catch
                {
                    // ignored
                }
            },
            AvailableClickthrough = true
        });

        fflogsClientIdInput = Configuration.FFLogsClientId;
        fflogsClientSecretInput = Configuration.FFLogsClientSecret;
        tomestoneApiKeyInput = Configuration.TomestoneApiKey;
    }

    private Configuration Configuration => plugin.Configuration;

    private static Vector4 Muted => M3.Alpha(M3.Scheme.OnSurfaceVariant, 0.9f);

    /// <summary>Horizontal padding inside <see cref="M3SettingRow"/>, used to line other content up with row text.</summary>
    private static float RowInset => 12f * M3.Scale;

    /// <summary>Tomestone.gg has no data for the Korean data centres, so its page is hidden on the KR client.</summary>
    private static bool TomestoneAvailable => !PassportCheckerReborn.IsKoreanClient;

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    public override void PreDraw()
    {
        theme = M3Style.Push();
    }

    public override void PostDraw()
    {
        theme?.Dispose();
        theme = null;
    }

    public override void OnClose()
    {
        if (savePending)
        {
            Configuration.Save();
            savePending = false;
        }

        M3Motion.Reset();
        M3CardHost.Reset();
    }

    public override void Draw()
    {
        // Body text follows the text size setting.
        using var bodyFont = ImRaii.PushFont(M3.Body);

        DrawBackdrop();

        if (page == Page.Tomestone && !TomestoneAvailable)
        {
            page = Page.General;
        }

        var scale = M3.Scale;
        var available = ImGui.GetContentRegionAvail();
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var navWidth = (available.X >= DrawerLayoutWidth * scale ? DrawerWidth : RailWidth) * scale;

        var origin = ImGui.GetCursorScreenPos();
        var dividerX = origin.X + navWidth + (spacing * 0.5f);
        ImGui.GetWindowDrawList().AddLine(
            new Vector2(dividerX, origin.Y),
            new Vector2(dividerX, origin.Y + available.Y),
            M3.U32(M3.Scheme.OutlineVariant, 0.45f), 1f * scale);

        DrawNavigation(navWidth);
        ImGui.SameLine(0f, spacing);
        DrawBody();

        if (savePending && !ImGui.IsAnyItemActive())
        {
            Configuration.Save();
            savePending = false;
        }
    }

    /// <summary>A soft tonal wash across the top of the window.</summary>
    private static void DrawBackdrop()
    {
        var s = M3.Scheme;
        var windowPos = ImGui.GetWindowPos();
        var top = windowPos.Y + ImGui.GetCursorStartPos().Y - ImGui.GetStyle().WindowPadding.Y;

        M3Draw.VerticalGradient(ImGui.GetWindowDrawList(),
            new Vector2(windowPos.X, top),
            new Vector2(windowPos.X + ImGui.GetWindowSize().X, top + (200f * M3.Scale)),
            M3.Alpha(s.SurfaceContainer, 0.65f), M3.Alpha(s.Surface, 0f));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Navigation
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawNavigation(float width)
    {
        using var child = ImRaii.Child("##pcr_nav", new Vector2(width, -1f), false, ImGuiWindowFlags.NoScrollbar);
        if (!child)
        {
            return;
        }

        var expanded = ImGui.GetContentRegionAvail().X >= M3Navigation.DrawerBreakpoint * M3.Scale;

        DrawBrand(expanded);
        ImGui.Dummy(new Vector2(0f, M3.Space2));

        var clicked = M3Navigation.Draw("pcr_nav", BuildNavItems(), expanded);
        if (clicked != null && Enum.TryParse<Page>(clicked, out var target))
        {
            page = target;
        }

        if (expanded)
        {
            DrawVersionFooter();
        }
    }

    private List<M3NavItem> BuildNavItems()
    {
        var cfg = Configuration;
        var fflogsNeedsSetup = cfg.EnableFFLogsIntegrationOverlay && !HasFFLogsCredentials(cfg);
        var tomestoneNeedsSetup = cfg.EnableTomestoneIntegration && string.IsNullOrEmpty(cfg.TomestoneApiKey);

        var items = new List<M3NavItem>
        {
            new(nameof(Page.General), Loc.T("General"), FontAwesomeIcon.SlidersH, page == Page.General,
                Loc.T("Party Finder quality-of-life options")),
            new(nameof(Page.Overlays), Loc.T("Overlays"), FontAwesomeIcon.LayerGroup, page == Page.Overlays,
                Loc.T("Member info and party list overlays")),
            new(nameof(Page.FFLogs), "FFLogs", FontAwesomeIcon.ChartBar, page == Page.FFLogs,
                Loc.T("FFLogs integration and API client"), Badge: fflogsNeedsSetup ? "!" : null),
        };

        if (TomestoneAvailable)
        {
            items.Add(new(nameof(Page.Tomestone), "Tomestone", FontAwesomeIcon.Gem, page == Page.Tomestone,
                Loc.T("Tomestone.gg integration and API key"), Badge: tomestoneNeedsSetup ? "!" : null));
        }

        items.Add(new(nameof(Page.PlayerTrack), "PlayerTrack", FontAwesomeIcon.AddressBook, page == Page.PlayerTrack,
            Loc.T("Resolve hidden names from PlayerTrack"), SeparatorAfter: true));
        items.Add(new(nameof(Page.Appearance), Loc.T("Appearance"), FontAwesomeIcon.Palette, page == Page.Appearance,
            Loc.T("Language, text size and accent color")));
        items.Add(new(nameof(Page.About), Loc.T("About"), FontAwesomeIcon.InfoCircle, page == Page.About,
            Loc.T("Commands, caches and links")));

        return items;
    }

    /// <summary>The plugin's mark at the top of the navigation column; a shortcut to the About page.</summary>
    private void DrawBrand(bool expanded)
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var width = MathF.Max(1f, ImGui.GetContentRegionAvail().X);
        var badge = 40f * scale;
        var height = badge + (8f * scale);

        var pressed = ImGui.InvisibleButton("##pcr_brand", new Vector2(width, height));
        var hovered = ImGui.IsItemHovered();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();

        if (hovered)
        {
            drawList.AddRectFilled(min, max, M3.U32(s.OnSurface, M3.StateHover), M3.ShapeMedium);
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        }

        var center = expanded
            ? new Vector2(min.X + (8f * scale) + (badge * 0.5f), min.Y + (height * 0.5f))
            : new Vector2(min.X + (width * 0.5f), min.Y + (height * 0.5f));
        var half = new Vector2(badge * 0.5f);
        drawList.AddCircleFilled(center, badge * 0.5f, M3.U32(s.PrimaryContainer), 32);
        M3Draw.IconCentered(drawList, FontAwesomeIcon.Passport, center - half, center + half, s.OnPrimaryContainer);

        if (expanded)
        {
            const string tagline = "REBORN · CUSTOM";
            var textX = center.X + (badge * 0.5f) + (10f * scale);
            var textWidth = MathF.Max(16f * scale, max.X - textX - (4f * scale));
            var gap = 1f * scale;

            string name;
            Vector2 nameSize;
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                name = M3Navigation.Truncate("Passport Checker", textWidth);
                nameSize = ImGui.CalcTextSize(name);
            }

            Vector2 taglineSize;
            using (ImRaii.PushFont(M3.LabelSmall))
            {
                taglineSize = ImGui.CalcTextSize(tagline);
            }

            var textTop = min.Y + ((height - nameSize.Y - gap - taglineSize.Y) * 0.5f);
            using (ImRaii.PushFont(M3.TitleMedium))
            {
                drawList.AddText(new Vector2(textX, textTop), M3.U32(s.OnSurface, 0.98f), name);
            }

            using (ImRaii.PushFont(M3.LabelSmall))
            {
                drawList.AddText(new Vector2(textX, textTop + nameSize.Y + gap), M3.U32(s.Primary), tagline);
            }
        }

        if (hovered)
        {
            ImguiTooltips.ShowTooltip(Loc.T("About Passport Checker Reborn"));
        }

        if (pressed)
        {
            page = Page.About;
        }
    }

    /// <summary>The installed version, pinned to the bottom of the navigation column.</summary>
    private void DrawVersionFooter()
    {
        var label = $"v{version}";
        var size = M3Widgets.PillSize(label, FontAwesomeIcon.CodeBranch);
        var bottom = ImGui.GetCursorPosY() + ImGui.GetContentRegionAvail().Y - size.Y;
        ImGui.SetCursorPosY(MathF.Max(ImGui.GetCursorPosY() + M3.Space2, bottom));
        _ = M3Widgets.Pill("##pcr_version", label, M3.Scheme.OnSurfaceVariant, FontAwesomeIcon.CodeBranch, Loc.T("Installed version"));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Body
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawBody()
    {
        using var body = ImRaii.Child("##pcr_body", new Vector2(-1f, -1f), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        if (!body)
        {
            return;
        }

        DrawTopAppBar();

        // One child per page, so each page keeps its own scroll position.
        using var content = ImRaii.Child($"##pcr_page_{page}", new Vector2(-1f, -1f), false);
        if (!content)
        {
            return;
        }

        switch (page)
        {
            case Page.General:
                DrawGeneralPage();
                break;
            case Page.Overlays:
                DrawOverlaysPage();
                break;
            case Page.FFLogs:
                DrawFFLogsPage();
                break;
            case Page.Tomestone:
                DrawTomestonePage();
                break;
            case Page.PlayerTrack:
                DrawPlayerTrackPage();
                break;
            case Page.Appearance:
                DrawAppearancePage();
                break;
            case Page.About:
                DrawAboutPage();
                break;
        }

        ImGui.Dummy(new Vector2(0f, M3.Space2));
    }

    private static (string Title, string Subtitle) PageHeading(Page page) => page switch
    {
        Page.General => (Loc.T("General"), Loc.T("Party Finder quality-of-life options")),
        Page.Overlays => (Loc.T("Overlays"), Loc.T("What the member info and party list overlays show")),
        Page.FFLogs => ("FFLogs", Loc.T("Clears and parses from FFLogs")),
        Page.Tomestone => ("Tomestone", Loc.T("Prog points and clears from Tomestone.gg")),
        Page.PlayerTrack => ("PlayerTrack", Loc.T("Resolve hidden names from PlayerTrack")),
        Page.Appearance => (Loc.T("Appearance"), Loc.T("Language and how the plugin's windows look")),
        _ => (Loc.T("About"), Loc.T("Commands, caches and links")),
    };

    /// <summary>The page title and subtitle above the scrolling content, with a divider beneath.</summary>
    private void DrawTopAppBar()
    {
        var s = M3.Scheme;
        var scale = M3.Scale;
        var (title, subtitle) = PageHeading(page);
        var width = MathF.Max(64f * scale, ImGui.GetContentRegionAvail().X);

        // Measured rather than placed at fixed offsets: the font's line height does not track
        // the UI scale, so hard-coded offsets overlap.
        var padTop = 6f * scale;
        var padBottom = 8f * scale;
        var lineGap = 2f * scale;

        string clippedTitle;
        Vector2 titleSize;
        using (ImRaii.PushFont(M3.HeadlineSmall))
        {
            clippedTitle = M3Navigation.Truncate(title, width);
            titleSize = ImGui.CalcTextSize(clippedTitle);
        }

        string clippedSubtitle;
        Vector2 subtitleSize;
        using (ImRaii.PushFont(M3.BodySmall))
        {
            clippedSubtitle = M3Navigation.Truncate(subtitle, width);
            subtitleSize = ImGui.CalcTextSize(clippedSubtitle);
        }

        var contentHeight = titleSize.Y + lineGap + subtitleSize.Y;
        var height = MathF.Max(52f * scale, padTop + contentHeight + padBottom);
        ImGui.Dummy(new Vector2(width, height));

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var drawList = ImGui.GetWindowDrawList();
        var textTop = min.Y + ((height - contentHeight) * 0.5f);

        using (ImRaii.PushFont(M3.HeadlineSmall))
        {
            drawList.AddText(new Vector2(min.X, textTop), M3.U32(s.OnSurface, 0.98f), clippedTitle);
        }

        using (ImRaii.PushFont(M3.BodySmall))
        {
            drawList.AddText(new Vector2(min.X, textTop + titleSize.Y + lineGap), M3.U32(s.OnSurfaceVariant, 0.88f), clippedSubtitle);
        }

        drawList.AddLine(new Vector2(min.X, max.Y), max, M3.U32(s.OutlineVariant, 0.5f), 1f * scale);
        ImGui.Dummy(new Vector2(width, M3.Space2));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // General
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawGeneralPage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("general_details", Loc.T("Listing details"), FontAwesomeIcon.AddressCard))
        {
            var jobIcons = cfg.ShowPartyJobIcons;
            if (SwitchRow(Loc.T("Show job icons"), Loc.T("Shows each player's job icon in the member info and party list overlays."), ref jobIcons))
            {
                cfg.ShowPartyJobIcons = jobIcons;
                cfg.Save();
            }

            var keepOpen = cfg.PreventAutoClosingOnPartyChanges2;
            if (SwitchRow(Loc.T("Keep the Party Finder window open when the party changes"),
                    Loc.T("The game normally closes the Party Finder detail window when your party composition changes; this keeps it (and the member overlay) open."),
                    ref keepOpen))
            {
                cfg.PreventAutoClosingOnPartyChanges2 = keepOpen;
                cfg.Save();
            }
        }

        using (M3Card.Begin("general_listings", Loc.T("Listings"), FontAwesomeIcon.ListUl))
        {
            var autoRefresh = cfg.EnableAutomaticRefresh;
            if (SwitchRow(Loc.T("Refresh listings automatically"), Loc.T("Reloads the Party Finder list on a timer while you're browsing it."), ref autoRefresh))
            {
                cfg.EnableAutomaticRefresh = autoRefresh;
                cfg.Save();
            }

            if (cfg.EnableAutomaticRefresh)
            {
                using var group = M3SubGroup.Begin();
                var interval = cfg.AutoRefreshIntervalSeconds;
                if (SliderRow(Loc.T("Refresh interval"), null, ref interval, 10, 120, Loc.T(" s")))
                {
                    cfg.AutoRefreshIntervalSeconds = interval;
                    savePending = true;
                }
            }

            var rightClick = cfg.RightClickPlayerNameForRecruitment3;
            if (SwitchRow(Loc.T("Right-Click Player Name to View Their Recruitment"),
                    Loc.T("Adds a 'View Recruitment' option when you right-click a player. If they're hosting a Party Finder listing, it finds and opens it."),
                    ref rightClick))
            {
                cfg.RightClickPlayerNameForRecruitment3 = rightClick;
                cfg.Save();

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
        }

        using (M3Card.Begin("general_blacklist", Loc.T("Blacklist"), FontAwesomeIcon.UserSlash))
        {
            var blacklist = cfg.EnableBlacklistFeature;
            if (SwitchRow(Loc.T("Flag blacklisted players"), Loc.T("Marks players on your in-game blacklist with a BL tag in the member info overlay."), ref blacklist))
            {
                cfg.EnableBlacklistFeature = blacklist;
                cfg.Save();
            }

            if (ButtonRow(Loc.T("Refresh blacklist"), Loc.T("Re-reads the blacklist from the game and saves the result."),
                    "##blacklist_refresh", Loc.T("Refresh"), FontAwesomeIcon.Sync))
            {
                plugin.PartyFinderManager.ForceRefreshBlacklist();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Overlays
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawOverlaysPage()
    {
        var cfg = Configuration;

        using (M3Card.Begin("overlays_member_info", Loc.T("Member info overlay"), FontAwesomeIcon.Users,
                   subtitle: Loc.T("Appears beside a Party Finder listing's details.")))
        {
            var show = cfg.ShowMemberInfoOverlay;
            if (SwitchRow(Loc.T("Show member info overlay"), null, ref show))
            {
                cfg.ShowMemberInfoOverlay = show;
                cfg.Save();
            }

            if (cfg.ShowMemberInfoOverlay)
            {
                using var group = M3SubGroup.Begin();

                var highEnd = cfg.OnlyShowOverlayForHighEndDuties;
                if (SwitchRow(Loc.T("High-end duties only"), Loc.T("Hides the overlay for listings that aren't high-end duties."), ref highEnd))
                {
                    cfg.OnlyShowOverlayForHighEndDuties = highEnd;
                    cfg.Save();
                }

                M3Segment[] sides =
                [
                    new(Loc.T("Left"), FontAwesomeIcon.ArrowLeft),
                    new(Loc.T("Right"), FontAwesomeIcon.ArrowRight),
                ];
                var side = SegmentedRow(Loc.T("Side"), Loc.T("Which side of the listing details the overlay sits on."),
                    "overlay_side", sides, cfg.ShowOverlayOnLeftSide ? 0 : 1);
                if (side >= 0)
                {
                    cfg.ShowOverlayOnLeftSide = side == 0;
                    cfg.Save();
                }

                var resolvedNames = cfg.ShowResolvedPlayerNames;
                if (SwitchRow(Loc.T("Show resolved player names"),
                        Loc.T("Shows Name@World once a player is resolved, instead of \"Player 1\"."), ref resolvedNames))
                {
                    cfg.ShowResolvedPlayerNames = resolvedNames;
                    cfg.Save();
                }
            }
        }

        // Name freshness — re-verify stale cached names against the adventure plate. Independent of the
        // member overlay (it maintains the shared name cache), so it stays available regardless.
        using (M3Card.Begin("overlays_name_freshness", Loc.T("Name freshness"), FontAwesomeIcon.History,
                   subtitle: Loc.T("Keeps cached player names up to date.")))
        {
            var reverify = cfg.EnableStaleNameReverification;
            if (SwitchRow(Loc.T("Re-verify stale player names via adventure plate"),
                    Loc.T("When a cached name is older than the threshold below, quietly re-checks it against the player's adventure plate. Throttled by the cooldown so the same stale name isn't re-checked constantly; detected renames are recorded in the name history."),
                    ref reverify))
            {
                cfg.EnableStaleNameReverification = reverify;
                cfg.Save();
            }

            if (cfg.EnableStaleNameReverification)
            {
                using var group = M3SubGroup.Begin();

                var staleDays = cfg.StaleNameThresholdDays;
                if (SliderRow(Loc.T("Stale after"), null, ref staleDays, 1, 90, Loc.T(" days")))
                {
                    cfg.StaleNameThresholdDays = staleDays;
                    savePending = true;
                }

                var cooldownHours = cfg.ReverifyCooldownHours;
                if (SliderRow(Loc.T("Retry cooldown"), null, ref cooldownHours, 1, 168, Loc.T(" h")))
                {
                    cfg.ReverifyCooldownHours = cooldownHours;
                    savePending = true;
                }
            }

            var privateCd = cfg.PrivatePlayerReverifyCooldownHours;
            if (SliderRow(Loc.T("Re-check hidden (Private) players every"),
                    Loc.T("How often to re-attempt an adventure-plate lookup for players whose plate is hidden. Higher = fewer wasted requests, but slower to notice if they make their plate public."),
                    ref privateCd, 1, 72, Loc.T(" h")))
            {
                cfg.PrivatePlayerReverifyCooldownHours = privateCd;
                savePending = true;
            }
        }

        if (cfg.ShowPartyListOverlay && !cfg.EnableFFLogsIntegrationOverlay && !cfg.EnableTomestoneIntegration)
        {
            M3Widgets.Banner("##party_list_needs_source",
                Loc.T("The party list overlay stays hidden until FFLogs or Tomestone is turned on."),
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("overlays_party_list", Loc.T("Party list overlay"), FontAwesomeIcon.UserFriends,
                   subtitle: Loc.T("FFLogs and Tomestone data for your current party, next to the party list. Includes a duty picker for encounter-specific lookups.")))
        {
            var show = cfg.ShowPartyListOverlay;
            if (SwitchRow(Loc.T("Show party list overlay"), Loc.T("You can also toggle it with /pcrparty."), ref show))
            {
                cfg.ShowPartyListOverlay = show;
                cfg.Save();
            }

            if (cfg.ShowPartyListOverlay)
            {
                using var group = M3SubGroup.Begin();

                var positionNames = new string[OverlayPositionNames.Length];
                for (var i = 0; i < positionNames.Length; i++)
                {
                    positionNames[i] = Loc.T(OverlayPositionNames[i]);
                }

                var position = (int)cfg.PartyListOverlayPosition;
                if (ComboRow(Loc.T("Position"), Loc.T("Where the overlay sits relative to the party list. Unbound lets you drag it anywhere."),
                        "##party_list_position", ref position, positionNames, 150f * M3.Scale))
                {
                    cfg.PartyListOverlayPosition = (PartyListOverlayPosition)position;
                    cfg.Save();
                }

                var hideInDuty = cfg.HidePartyListInDuty;
                if (SwitchRow(Loc.T("Hide in duties"), null, ref hideInDuty))
                {
                    cfg.HidePartyListInDuty = hideInDuty;
                    cfg.Save();
                }

                var hideInCombat = cfg.HidePartyListInCombat;
                if (SwitchRow(Loc.T("Hide in combat"), null, ref hideInCombat))
                {
                    cfg.HidePartyListInCombat = hideInCombat;
                    cfg.Save();
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // FFLogs
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawFFLogsPage()
    {
        var cfg = Configuration;
        var s = M3.Scheme;

        if (cfg.EnableFFLogsIntegrationOverlay && !HasFFLogsCredentials(cfg))
        {
            M3Widgets.Banner("##fflogs_missing", Loc.T("FFLogs is turned on, but no API client is saved yet. Add one below."),
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("fflogs_integration", Loc.T("Integration"), FontAwesomeIcon.ChartBar))
        {
            var enabled = cfg.EnableFFLogsIntegrationOverlay;
            if (SwitchRow(Loc.T("Show FFLogs data in overlays"),
                    Loc.T("Adds an FFLogs column with clears and parses to the member info and party list overlays."), ref enabled))
            {
                cfg.EnableFFLogsIntegrationOverlay = enabled;
                cfg.Save();
            }

            var autoFetch = cfg.AutoFetchFFLogsWhenResolved;
            if (SwitchRow(Loc.T("Automatically look up FFLogs data once all names are resolved"),
                    Loc.T("Runs a lookup automatically when every name resolves. Spends FFLogs API points (see below)."), ref autoFetch))
            {
                cfg.AutoFetchFFLogsWhenResolved = autoFetch;
                cfg.Save();
            }
        }

        using (M3Card.Begin("fflogs_client", Loc.T("API client"), FontAwesomeIcon.Key,
                   subtitle: Loc.T("Create a client on FFLogs, then paste its ID and secret here.")))
        {
            TextFieldRow(Loc.T("Client ID"), "##fflogs_client_id", Loc.T("Paste your client ID"), ref fflogsClientIdInput, 128);
            TextFieldRow(Loc.T("Client Secret"), "##fflogs_client_secret", Loc.T("Paste your client secret"), ref fflogsClientSecretInput, 128, password: true);

            var testing = fflogsTestInProgress;
            BeginActionRow();
            if (M3Widgets.Button("##fflogs_save", testing ? Loc.T("Testing…") : Loc.T("Save & test"), M3ButtonStyle.Filled,
                    testing ? FontAwesomeIcon.HourglassHalf : FontAwesomeIcon.Plug, enabled: !testing))
            {
                SaveAndTestFFLogsCredentials();
            }

            var dirty = fflogsClientIdInput != cfg.FFLogsClientId || fflogsClientSecretInput != cfg.FFLogsClientSecret;
            if (testing)
            {
                StatusLine(FontAwesomeIcon.HourglassHalf, Loc.T("Checking your credentials with FFLogs…"), Muted);
            }
            else if (dirty)
            {
                StatusLine(FontAwesomeIcon.PencilAlt, Loc.T("Unsaved changes."), s.Warning);
            }
            else if (!string.IsNullOrEmpty(fflogsTestMessage))
            {
                StatusLine(fflogsTestSucceeded ? FontAwesomeIcon.CheckCircle : FontAwesomeIcon.TimesCircle,
                    fflogsTestMessage, fflogsTestSucceeded ? s.Success : s.Error);
            }
        }

        if (HasFFLogsCredentials(cfg))
        {
            DrawFFLogsUsageCard();
        }

        using (var guide = M3ExpandableCard.Begin("fflogs_guide", Loc.T("How to get API credentials"), ref fflogsGuideExpanded,
                   FontAwesomeIcon.QuestionCircle))
        {
            if (guide.Expanded)
            {
                if (StepRow(1, Loc.T("Open the FFLogs API clients page."), "##fflogs_step_open", Loc.T("Open"), FontAwesomeIcon.ExternalLinkAlt))
                {
                    OpenUrl(FFLogsClientsUrl);
                }

                StepRow(2, Loc.T("Click \"Create Client\" in the top-right corner."));
                CopyStepRow(3, string.Format(Loc.T("Enter a client name, such as {0}."), FFLogsExampleClientName), "##fflogs_step_name", FFLogsExampleClientName);
                CopyStepRow(4, string.Format(Loc.T("Enter any redirect URL, such as {0}"), FFLogsExampleRedirectUrl), "##fflogs_step_redirect", FFLogsExampleRedirectUrl);
                StepRow(5, Loc.T("Leave \"Public Client\" unchecked."));
                StepRow(6, Loc.T("Copy the generated client ID and secret into the fields above."));
                StepRow(7, Loc.T("Click Save & test to check them."));
                StatusLine(FontAwesomeIcon.ShieldAlt, Loc.T("The client secret is only shown once. Keep it private."), s.Warning);
            }
        }
    }

    /// <summary>FFLogs API points spent this hour, shown once credentials are saved.</summary>
    private void DrawFFLogsUsageCard()
    {
        using var card = M3Card.Begin("fflogs_usage", Loc.T("API Usage"), FontAwesomeIcon.TachometerAlt);

        // Prefer the counters piggybacked onto recent FFLogs traffic; only spend a dedicated request to
        // seed the display when no query has carried them yet.
        var usage = plugin.FFLogsService.GetCachedRateLimit();
        var text = usage is { } u
            ? FormatFFLogsUsage(u.PointsSpentThisHour, u.LimitPerHour, u.PointsResetInSeconds)
            : fflogsUsageInProgress ? Loc.T("Checking…") : fflogsUsageText;

        if (usage is null && !fflogsUsageRequested && !fflogsUsageInProgress)
        {
            fflogsUsageRequested = true;
            fflogsUsageInProgress = true;
            _ = RefreshFFLogsUsageAsync();
        }

        if (ButtonRow(Loc.T("Points this hour"), text, "##fflogs_usage_refresh", Loc.T("Refresh"), FontAwesomeIcon.Sync))
        {
            fflogsUsageInProgress = true;
            _ = RefreshFFLogsUsageAsync();
        }
    }

    private void SaveAndTestFFLogsCredentials()
    {
        Configuration.FFLogsClientId = fflogsClientIdInput;
        Configuration.FFLogsClientSecret = fflogsClientSecretInput;
        Configuration.Save();
        fflogsTestMessage = string.Empty;
        fflogsTestInProgress = true;
        fflogsUsageRequested = false;  // re-fetch usage with the new credentials

        _ = TestFFLogsCredentialsAsync().ContinueWith(
            t => PassportCheckerReborn.Log.Warning(t.Exception, "[PassportCheckerReborn] Unhandled error in credential test."),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task TestFFLogsCredentialsAsync()
    {
        try
        {
            var accepted = await plugin.FFLogsService.TestCredentialsAsync(
                Configuration.FFLogsClientId,
                Configuration.FFLogsClientSecret);

            // The outcome is written before the message: the UI only reads the outcome once a message exists.
            fflogsTestSucceeded = accepted;
            fflogsTestMessage = accepted ? Loc.T("FFLogs accepted these credentials.") : Loc.T("FFLogs rejected these credentials.");
        }
        catch (Exception ex)
        {
            fflogsTestSucceeded = false;
            fflogsTestMessage = string.Format(Loc.T("The test failed: {0}"), ex.Message);
            PassportCheckerReborn.Log.Warning(ex, "[PassportCheckerReborn] FFLogs credential test failed.");
        }
        finally
        {
            fflogsTestInProgress = false;
        }
    }

    private static string FormatFFLogsUsage(double spent, int limit, int resetSeconds)
        => string.Format(Loc.T("{0} / {1} points used this hour (resets in {2} min)"),
            spent.ToString("F0"), limit, Math.Max(0, resetSeconds / 60));

    private async Task RefreshFFLogsUsageAsync()
    {
        try
        {
            var rl = await plugin.FFLogsService.GetRateLimitAsync();
            fflogsUsageText = rl is { } r
                ? FormatFFLogsUsage(r.PointsSpentThisHour, r.LimitPerHour, r.PointsResetInSeconds)
                : Loc.T("Could not retrieve API usage.");
        }
        catch (Exception ex)
        {
            fflogsUsageText = Loc.T("Could not retrieve API usage.");
            PassportCheckerReborn.Log.Warning(ex, "[PassportCheckerReborn] FFLogs usage check failed.");
        }
        finally
        {
            fflogsUsageInProgress = false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tomestone
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawTomestonePage()
    {
        var cfg = Configuration;
        var s = M3.Scheme;

        if (cfg.EnableTomestoneIntegration && string.IsNullOrEmpty(cfg.TomestoneApiKey))
        {
            M3Widgets.Banner("##tomestone_missing", Loc.T("Tomestone is turned on, but no API key is saved yet. Add one below."),
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("tomestone_integration", Loc.T("Integration"), FontAwesomeIcon.Gem))
        {
            var enabled = cfg.EnableTomestoneIntegration;
            if (SwitchRow(Loc.T("Show Tomestone data in overlays"),
                    Loc.T("Adds a Tomestone column with prog points and clears. In the member info overlay, click Tomestone to look up the listing's duty."),
                    ref enabled))
            {
                cfg.EnableTomestoneIntegration = enabled;
                cfg.Save();
            }
        }

        using (M3Card.Begin("tomestone_key", Loc.T("API access"), FontAwesomeIcon.Key,
                   subtitle: Loc.T("Your Tomestone.gg access token, sent with each lookup.")))
        {
            TextFieldRow(Loc.T("API key"), "##tomestone_api_key", Loc.T("Paste your access token"), ref tomestoneApiKeyInput, 256, password: true);

            BeginActionRow();
            if (M3Widgets.Button("##tomestone_save", Loc.T("Save"), M3ButtonStyle.Filled, FontAwesomeIcon.Save))
            {
                cfg.TomestoneApiKey = tomestoneApiKeyInput;
                cfg.Save();
            }

            if (tomestoneApiKeyInput != cfg.TomestoneApiKey)
            {
                StatusLine(FontAwesomeIcon.PencilAlt, Loc.T("Unsaved changes."), s.Warning);
            }
            else if (!string.IsNullOrEmpty(cfg.TomestoneApiKey))
            {
                StatusLine(FontAwesomeIcon.CheckCircle, Loc.T("API key saved."), s.Success);
            }
        }

        using (var guide = M3ExpandableCard.Begin("tomestone_guide", Loc.T("How to get an API key"), ref tomestoneGuideExpanded,
                   FontAwesomeIcon.QuestionCircle))
        {
            if (guide.Expanded)
            {
                if (StepRow(1, Loc.T("Open your Tomestone account settings."), "##tomestone_step_open", Loc.T("Open"), FontAwesomeIcon.ExternalLinkAlt))
                {
                    OpenUrl(TomestoneAccountUrl);
                }

                StepRow(2, Loc.T("Scroll down to the \"API access token\" section."));
                StepRow(3, Loc.T("Click \"Generate access token\"."));
                StepRow(4, Loc.T("Paste the token into the field above."));
                StepRow(5, Loc.T("Click Save."));
                StatusLine(FontAwesomeIcon.ShieldAlt, Loc.T("Keep your token private. It grants access to your Tomestone account data."), s.Warning);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PlayerTrack
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawPlayerTrackPage()
    {
        var cfg = Configuration;
        var s = M3.Scheme;
        var svc = plugin.PlayerTrackService;
        var (installed, loaded) = svc.GetPluginStatus();
        var dbExists = svc.DatabaseExists;

        if (!dbExists)
        {
            M3Widgets.Banner("##playertrack_no_db",
                Loc.T("Integration is inactive: the PlayerTrack database was not found.") + " " +
                Loc.T("Install and run PlayerTrack at least once so it builds its database, then reopen this window."),
                M3Severity.Warning, FontAwesomeIcon.ExclamationTriangle);
            ImGui.Dummy(new Vector2(0f, M3.Space2));
        }

        using (M3Card.Begin("playertrack_status", Loc.T("Status"), FontAwesomeIcon.Stethoscope,
                   subtitle: Loc.T(
                       "When enabled, party members whose name can't be read from Party Finder packets or the " +
                       "adventure plate are looked up in the PlayerTrack plugin's local database (read-only). " +
                       "This can recover names of players who hide their adventure plate, as long as you have " +
                       "encountered them before.")))
        {
            StatusRow(installed ? Loc.T("PlayerTrack plugin installed") : Loc.T("PlayerTrack plugin not found"),
                null, installed ? M3Severity.Success : M3Severity.Error);
            StatusRow(loaded ? Loc.T("PlayerTrack is loaded") : Loc.T("PlayerTrack not currently loaded"),
                null, loaded ? M3Severity.Success : M3Severity.Neutral);
            StatusRow(dbExists ? Loc.T("Database found") : Loc.T("Database not found"),
                dbExists ? svc.DatabasePath : null, dbExists ? M3Severity.Success : M3Severity.Error);
        }

        using (M3Card.Begin("playertrack_resolution", Loc.T("Name resolution"), FontAwesomeIcon.UserCheck))
        {
            var enabled = cfg.EnablePlayerTrackIntegration;
            if (SwitchRow(Loc.T("Enable PlayerTrack name resolution"),
                    Loc.T("Reads PlayerTrack's database (read-only) to resolve otherwise-unknown party member names."), ref enabled))
            {
                cfg.EnablePlayerTrackIntegration = enabled;
                cfg.Save();
            }

            if (cfg.EnablePlayerTrackIntegration)
            {
                using var group = M3SubGroup.Begin();

                M3Segment[] priorities =
                [
                    new(Loc.T("Adventure Plate first (freshest)"), FontAwesomeIcon.IdCard),
                    new(Loc.T("PlayerTrack first (fastest)"), FontAwesomeIcon.Bolt),
                ];
                var supporting = cfg.PlayerTrackPriority == PlayerTrackResolutionPriority.CharaCardFirst
                    ? Loc.T(
                        "Tries the live adventure plate first (most up-to-date name). " +
                        "If the plate is hidden or the lookup fails, falls back to PlayerTrack.")
                    : Loc.T(
                        "Uses PlayerTrack's stored name first (instant, no network request, works for hidden plates). " +
                        "Only queries the adventure plate when PlayerTrack has no record. " +
                        "Note: PlayerTrack data can be stale if the player has since renamed or transferred worlds.");
                var picked = SegmentedRow(Loc.T("Resolution priority:").TrimEnd(':'), supporting, "pt_priority", priorities,
                    (int)cfg.PlayerTrackPriority);
                if (picked >= 0)
                {
                    cfg.PlayerTrackPriority = (PlayerTrackResolutionPriority)picked;
                    cfg.Save();
                }
            }

            StatusLine(FontAwesomeIcon.InfoCircle, Loc.T(
                "Names resolved via PlayerTrack are marked with a PT tag in the overlay. Hover a member's " +
                "name (or the tag) to see the name's source, how old the cached data is, and any previous names."), Muted);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Appearance
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawAppearancePage()
    {
        var cfg = Configuration;
        var scale = M3.Scale;

        using (M3Card.Begin("appearance_language", Loc.T("Language"), FontAwesomeIcon.Language))
        {
            var names = Enum.GetNames<PluginLanguage>();
            var display = new string[names.Length];
            for (var i = 0; i < names.Length; i++)
            {
                display[i] = Loc.T(names[i]);
            }

            var current = (int)cfg.Language;
            if (ComboRow(Loc.T("Language"), null, "##pcr_language", ref current, display, 150f * scale))
            {
                cfg.Language = (PluginLanguage)current;
                Loc.Language = cfg.Language;
                cfg.Save();
            }
        }

        using (M3Card.Begin("appearance_text", Loc.T("Text"), FontAwesomeIcon.Font))
        {
            // Fixed steps rather than a slider: every size is a separate font build with the full Hangul set.
            var selected = Array.FindIndex(TextScaleSteps, step => MathF.Abs(step - cfg.UiTextScale) < 0.01f);
            var segments = new M3Segment[TextScaleSteps.Length];
            for (var i = 0; i < TextScaleSteps.Length; i++)
            {
                segments[i] = new($"{TextScaleSteps[i] * 100f:F0}%");
            }

            var picked = SegmentedRow(Loc.T("Text size"),
                Loc.T("Size of the text in the plugin's windows, relative to Dalamud's default font."),
                "text_scale", segments, selected);
            if (picked >= 0)
            {
                cfg.UiTextScale = TextScaleSteps[picked];
                cfg.Save();
                M3.PreloadFonts();
            }
        }

        using (M3Card.Begin("appearance_theme", Loc.T("Theme"), FontAwesomeIcon.Palette,
                   subtitle: Loc.T("Every color in the plugin's windows is generated from one accent color.")))
        {
            var swatch = 28f * scale;
            var reset = M3Widgets.IconButtonSize;
            var controlSize = new Vector2(swatch + M3.Space2 + reset, MathF.Max(swatch, reset));

            var row = M3SettingRow.Begin(Loc.T("Accent color"), Loc.T("Very dark or grey colors fall back to the default."), controlSize);
            var origin = row.ControlPosition;

            ImGui.SetCursorScreenPos(origin + new Vector2(0f, (controlSize.Y - swatch) * 0.5f));
            var accent = cfg.UiAccentColor;
            if (M3Widgets.ColorSwatch("##accent_color", ref accent))
            {
                cfg.UiAccentColor = accent with { W = 1f };
                savePending = true;
            }

            ImGui.SetCursorScreenPos(origin + new Vector2(swatch + M3.Space2, (controlSize.Y - reset) * 0.5f));
            if (M3Widgets.IconButton("##accent_reset", FontAwesomeIcon.Undo, Loc.T("Reset to the default accent")))
            {
                cfg.UiAccentColor = M3.DefaultSeed;
                cfg.Save();
            }

            M3SettingRow.End(row);

            DrawAccentPresets();
        }
    }

    private void DrawAccentPresets()
    {
        var cfg = Configuration;
        var s = M3.Scheme;
        var scale = M3.Scale;
        var diameter = 28f * scale;
        var gap = M3.Space2;
        var controlWidth = (AccentPresets.Length * diameter) + ((AccentPresets.Length - 1) * gap);

        var row = M3SettingRow.Begin(Loc.T("Presets"), null, new Vector2(controlWidth, diameter));
        var origin = row.ControlPosition;
        var drawList = ImGui.GetWindowDrawList();

        for (var i = 0; i < AccentPresets.Length; i++)
        {
            var (name, color) = AccentPresets[i];
            ImGui.SetCursorScreenPos(origin + new Vector2(i * (diameter + gap), 0f));
            var pressed = ImGui.InvisibleButton($"##accent_preset_{i}", new Vector2(diameter));
            var hovered = ImGui.IsItemHovered();
            var center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) * 0.5f;

            drawList.AddCircleFilled(center, diameter * 0.5f, M3.U32(color), 32);
            if (IsSameColor(cfg.UiAccentColor, color))
            {
                drawList.AddCircle(center, (diameter * 0.5f) + (3f * scale), M3.U32(s.OnSurface), 32, 2f * scale);
            }
            else if (hovered)
            {
                drawList.AddCircle(center, (diameter * 0.5f) + (2f * scale), M3.U32(s.Outline), 32, 1.5f * scale);
            }

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImguiTooltips.ShowTooltip(Loc.T(name));
            }

            if (pressed)
            {
                cfg.UiAccentColor = color;
                cfg.Save();
            }
        }

        M3SettingRow.End(row);
    }

    private static bool IsSameColor(Vector4 a, Vector4 b)
    {
        const float tolerance = 0.002f;
        return MathF.Abs(a.X - b.X) < tolerance
            && MathF.Abs(a.Y - b.Y) < tolerance
            && MathF.Abs(a.Z - b.Z) < tolerance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // About
    // ─────────────────────────────────────────────────────────────────────────
    private void DrawAboutPage()
    {
        var s = M3.Scheme;

        using (M3Card.Begin("about_hero", null, style: M3CardStyle.Elevated))
        {
            using (ImRaii.PushFont(M3.TitleLarge))
            using (ImRaii.PushColor(ImGuiCol.Text, s.Primary))
            {
                ImGui.TextWrapped(Loc.T("Passport Checker Reborn") + " (Custom)");
            }

            ImGui.Dummy(new Vector2(0f, M3.Space1));

            using (ImRaii.PushColor(ImGuiCol.Text, M3.Alpha(s.OnSurfaceVariant, 0.95f)))
            {
                ImGui.TextWrapped(Loc.T(
                    "Passport Checker Reborn is an open-source alternative to the PFFinder plugin. " +
                    "It shows a member-info overlay alongside party finder listings, integrates with " +
                    "Tomestone.gg and FFLogs for quick prog-point lookups, and offers quality-of-life improvements " +
                    "to the party finder UI."));
            }

            ImGui.Dummy(new Vector2(0f, M3.Space2));

            _ = M3Widgets.Pill("##about_version", $"v{version}", s.Primary, FontAwesomeIcon.CodeBranch);
            ImGui.SameLine(0f, M3.Space1);
            _ = M3Widgets.Pill("##about_author", "The Combat Reborn Team - LTS", s.Tertiary, FontAwesomeIcon.Users);

            ImGui.Dummy(new Vector2(0f, M3.Space2));

            if (M3Widgets.Button("##about_kofi", Loc.T("Support on Ko-fi"), M3ButtonStyle.Tonal, FontAwesomeIcon.MugHot))
            {
                OpenUrl(KofiUrl);
            }

            ImGui.SameLine(0f, M3.Space2);

            if (M3Widgets.Button("##about_repo", Loc.T("Source code"), M3ButtonStyle.Outlined, FontAwesomeIcon.CodeBranch))
            {
                OpenUrl(RepoUrl);
            }

            ImGui.SameLine(0f, M3.Space2);

            if (M3Widgets.Button("##about_discord", Loc.T("Contact (Discord)"), M3ButtonStyle.Outlined, FontAwesomeIcon.Comments))
            {
                OpenUrl(DiscordUrl);
            }
        }

        using (M3Card.Begin("about_commands", Loc.T("Commands"), FontAwesomeIcon.Terminal))
        {
            TextRow("/pcr", Loc.T("Opens or closes this window. /pfchecker does the same."));
            TextRow("/pcrparty", Loc.T("Shows or hides the party list overlay."));
        }

        using (M3Card.Begin("about_markers", Loc.T("Overlay markers"), FontAwesomeIcon.Tags))
        {
            BadgeRow("PT", s.Info, Loc.T("Name recovered from the PlayerTrack database"));
            BadgeRow("BL", s.Error, Loc.T("On your in-game blacklist"));
            BadgeRow(Loc.T("Private"), s.OnSurfaceVariant, Loc.T("Adventure plate is hidden"));
            BadgeRow(Loc.T("Lookup failed"), s.Error, Loc.T("FFLogs request failed — refresh to retry"), asText: true);
        }

        using (M3Card.Begin("about_caches", Loc.T("Caches"), FontAwesomeIcon.Database))
        {
            // Resolved names are kept indefinitely by design, so guard the wipe behind SHIFT + a confirmation
            // dialog to make an accidental click impossible.
            var shiftHeld = ImGui.GetIO().KeyShift;
            if (CountButtonRow(Loc.T("Resolved players"), Loc.T("Content IDs already matched to a name and world."),
                    plugin.CidCache.Count, "##cid_clear", shiftHeld,
                    Loc.T(
                        "Deletes all stored Content ID → name/world mappings and their name history from disk. " +
                        "Names are re-learned as you encounter players again.\n" +
                        "Hold SHIFT and click to enable this button.")))
            {
                ImGui.OpenPopup(CidClearPopupId);
            }

            if (CountButtonRow(Loc.T("Blacklisted players"), Loc.T("Your in-game blacklist, as last read from the game."),
                    plugin.BlacklistCache.Count, "##blacklist_clear", true,
                    Loc.T("Clears the persisted blacklist cache, then re-reads from the game.")))
            {
                plugin.BlacklistCache.Clear();
                plugin.PartyFinderManager.ForceRefreshBlacklist();
            }

            DrawCidClearConfirmPopup();
        }
    }

    /// <summary>Confirmation modal for wiping the resolved-name (CID) cache. Opened from the About page.</summary>
    private void DrawCidClearConfirmPopup()
    {
        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));

        if (!ImGui.BeginPopupModal(Loc.T("Clear cached names?") + CidClearPopupId, ImGuiWindowFlags.AlwaysAutoResize))
        {
            return;
        }

        ImGui.TextUnformatted(string.Format(
            Loc.T("This permanently deletes all {0} stored names and their history. This cannot be undone."),
            plugin.CidCache.Count));
        ImGui.Dummy(new Vector2(0f, M3.Space2));

        var width = 120f * M3.Scale;
        if (M3Widgets.Button("##cid_clear_confirm", Loc.T("Delete"), M3ButtonStyle.Danger, FontAwesomeIcon.TrashAlt, width))
        {
            plugin.CidCache.Clear();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine(0f, M3.Space2);
        if (M3Widgets.Button("##cid_clear_cancel", Loc.T("Cancel"), M3ButtonStyle.Outlined, width: width))
        {
            ImGui.CloseCurrentPopup();
        }

        ImGui.EndPopup();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Row helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A setting with a trailing switch. Returns true when the user flipped it.</summary>
    private static bool SwitchRow(string label, string? supporting, ref bool value, bool enabled = true)
    {
        var row = M3SettingRow.Begin(label, supporting, M3Widgets.SwitchSize(), disabled: !enabled);
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var changed = M3Widgets.Switch($"##{label}_switch", ref value, enabled);
        M3SettingRow.End(row);
        return changed;
    }

    private static bool SliderRow(string label, string? supporting, ref int value, int min, int max, string suffix)
    {
        // Every slider shares one readout gutter sized for the widest unit, so the tracks line up with each
        // other instead of each shifting by the width of its own maximum value.
        var gutter = M3Widgets.SliderValueGutter($"{max}{suffix}");
        foreach (var unit in SliderUnits)
        {
            gutter = MathF.Max(gutter, M3Widgets.SliderValueGutter($"888{Loc.T(unit)}"));
        }

        var trackWidth = 150f * M3.Scale;
        var controlSize = new Vector2(trackWidth + gutter, M3Widgets.ButtonHeight);

        var row = M3SettingRow.Begin(label, supporting, controlSize);
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var changed = M3Widgets.SliderInt($"##{label}_slider", ref value, min, max, $"{value}{suffix}", trackWidth);
        M3SettingRow.End(row);
        return changed;
    }

    private static bool ComboRow(string label, string? supporting, string id, ref int index, IReadOnlyList<string> items, float width)
    {
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.ComboHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var changed = M3Widgets.Combo(id, ref index, items, width);
        M3SettingRow.End(row);
        return changed;
    }

    /// <summary>Returns the index the user picked this frame, or -1 when the selection did not change.</summary>
    private static int SegmentedRow(string label, string? supporting, string id, M3Segment[] segments, int selectedIndex)
    {
        var width = M3Widgets.SegmentedWidth(segments);
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.SegmentedHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var picked = M3Widgets.SegmentedButtons(id, segments, selectedIndex, width);
        M3SettingRow.End(row);
        return picked;
    }

    private static bool ButtonRow(string label, string? supporting, string id, string buttonLabel, FontAwesomeIcon icon)
    {
        var width = M3Widgets.ButtonWidth(icon, buttonLabel);
        var row = M3SettingRow.Begin(label, supporting, new Vector2(width, M3Widgets.ButtonHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var pressed = M3Widgets.Button(id, buttonLabel, M3ButtonStyle.Tonal, icon, width);
        M3SettingRow.End(row);
        return pressed;
    }

    /// <summary>A count with a trailing Clear button. Returns true when the (enabled) button was clicked.</summary>
    private static bool CountButtonRow(string label, string? supporting, int count, string id, bool enabled, string tooltip)
    {
        var s = M3.Scheme;
        var clearLabel = Loc.T("Clear");
        var countText = count.ToString();
        var countSize = ImGui.CalcTextSize(countText);
        var buttonWidth = M3Widgets.ButtonWidth(FontAwesomeIcon.TrashAlt, clearLabel);
        var controlSize = new Vector2(countSize.X + M3.Space3 + buttonWidth, M3Widgets.ButtonHeight);

        var row = M3SettingRow.Begin(label, supporting, controlSize);
        var origin = row.ControlPosition;

        ImGui.SetCursorScreenPos(origin + new Vector2(0f, (controlSize.Y - countSize.Y) * 0.5f));
        using (ImRaii.PushColor(ImGuiCol.Text, s.OnSurfaceVariant))
        {
            ImGui.TextUnformatted(countText);
        }

        ImGui.SetCursorScreenPos(origin + new Vector2(countSize.X + M3.Space3, 0f));
        var pressed = M3Widgets.Button(id, clearLabel, M3ButtonStyle.Danger, FontAwesomeIcon.TrashAlt, buttonWidth,
            enabled: enabled);

        // Shown while disabled too, since the tooltip is what explains how to enable it.
        ImguiTooltips.HoveredTooltip(tooltip);

        M3SettingRow.End(row);
        return pressed && enabled;
    }

    private static void TextFieldRow(string label, string id, string hint, ref string value, int maxLength, bool password = false)
    {
        var scale = M3.Scale;
        var width = Math.Clamp(ImGui.GetContentRegionAvail().X * 0.55f, 180f * scale, 340f * scale);
        var row = M3SettingRow.Begin(label, null, new Vector2(width, M3TextField.Height));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        M3TextField.Draw(id, hint, ref value, width, maxLength, password);
        M3SettingRow.End(row);
    }

    private static void TextRow(string label, string? supporting)
    {
        var row = M3SettingRow.Begin(label, supporting, Vector2.Zero);
        M3SettingRow.End(row);
    }

    /// <summary>A status line in a severity colour, with an optional muted detail line beneath.</summary>
    private static void StatusRow(string label, string? supporting, M3Severity severity)
    {
        var icon = severity switch
        {
            M3Severity.Success => FontAwesomeIcon.CheckCircle,
            M3Severity.Error => FontAwesomeIcon.TimesCircle,
            _ => FontAwesomeIcon.MinusCircle,
        };
        var color = severity == M3Severity.Neutral ? Muted : M3.Severity(severity);
        var row = M3SettingRow.Begin(label, supporting, Vector2.Zero, labelColor: color, leadingIcon: icon);
        M3SettingRow.End(row);
    }

    /// <summary>One overlay marker next to what it means: a badge, or plain coloured text when <paramref name="asText"/>.</summary>
    private static void BadgeRow(string badge, Vector4 accent, string meaning, bool asText = false)
    {
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + RowInset);
        if (asText)
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(accent, badge);
        }
        else
        {
            M3Badge.Draw(badge, accent);
        }

        ImGui.SameLine(0f, M3.Space2);
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(meaning);
    }

    /// <summary>One numbered instruction in a setup guide.</summary>
    private static void StepRow(int number, string text)
    {
        TextRow($"{number}.  {text}", null);
    }

    /// <summary>One numbered instruction with a trailing action button. Returns true when it was clicked.</summary>
    private static bool StepRow(int number, string text, string id, string action, FontAwesomeIcon icon, float? width = null)
    {
        var buttonWidth = width ?? M3Widgets.ButtonWidth(icon, action);
        var row = M3SettingRow.Begin($"{number}.  {text}", null, new Vector2(buttonWidth, M3Widgets.ButtonHeight));
        ImGui.SetCursorScreenPos(row.ControlPosition);
        var pressed = M3Widgets.Button(id, action, M3ButtonStyle.Tonal, icon, buttonWidth);
        M3SettingRow.End(row);
        return pressed;
    }

    /// <summary>A guide step whose button copies an example value, confirming briefly once it has.</summary>
    private void CopyStepRow(int number, string text, string id, string value)
    {
        var copied = copiedId == id && ImGui.GetTime() - copiedAt < CopiedFeedbackSeconds;
        var copyLabel = Loc.T("Copy");
        var copiedLabel = Loc.T("Copied");
        var width = MathF.Max(
            M3Widgets.ButtonWidth(FontAwesomeIcon.Copy, copyLabel),
            M3Widgets.ButtonWidth(FontAwesomeIcon.Check, copiedLabel));

        if (StepRow(number, text, id, copied ? copiedLabel : copyLabel, copied ? FontAwesomeIcon.Check : FontAwesomeIcon.Copy, width))
        {
            ImGui.SetClipboardText(value);
            copiedId = id;
            copiedAt = ImGui.GetTime();
        }
    }

    /// <summary>Starts a line of buttons beneath setting rows, lined up with the rows' text.</summary>
    private static void BeginActionRow()
    {
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + RowInset);
    }

    /// <summary>An icon and a wrapped line of text in one colour, lined up with the setting rows' text.</summary>
    private static void StatusLine(FontAwesomeIcon icon, string message, Vector4 color)
    {
        ImGui.Dummy(new Vector2(0f, M3.Space1));
        var start = ImGui.GetCursorScreenPos() + new Vector2(RowInset, 0f);
        var iconSize = M3Draw.MeasureIcon(icon);
        M3Draw.Icon(ImGui.GetWindowDrawList(), icon,
            new Vector2(start.X, start.Y + ((ImGui.GetTextLineHeight() - iconSize.Y) * 0.5f)), color);

        ImGui.SetCursorScreenPos(new Vector2(start.X + iconSize.X + M3.Space2, start.Y));
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(message);
    }

    private static bool HasFFLogsCredentials(Configuration cfg)
    {
        return !string.IsNullOrEmpty(cfg.FFLogsClientId) && !string.IsNullOrEmpty(cfg.FFLogsClientSecret);
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
}
