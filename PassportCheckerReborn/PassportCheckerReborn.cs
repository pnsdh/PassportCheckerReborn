using Dalamud.Game;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using PassportCheckerReborn.Services;
using PassportCheckerReborn.Windows;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn;

public sealed class PassportCheckerReborn : IAsyncDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPartyFinderGui PartyFinderGui { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;

    internal const string Version = "7.5.1.2";

    /// <summary>
    /// Whether the game is the Korean client. Dalamud's <see cref="ClientLanguage"/> enum has no
    /// Korean entry, so this is detected from game data (see <see cref="DetectKoreanClient"/>).
    /// Tomestone.gg has no Korean data, so its features are disabled when this is true.
    /// </summary>
    public static bool IsKoreanClient { get; private set; }

    private const string CommandName = "/pfchecker";
    public const string ALTCOMMAND = "/pcr";
    private const string PartyListCommandName = "/pcrparty";

    public Configuration Configuration { get; private set; } = null!;

    public readonly WindowSystem WindowSystem = new("PassportCheckerReborn");
    private MainWindow MainWindow { get; set; } = null!;
    internal PFWindow PFWindow { get; set; } = null!;
    internal PartyListWindow PartyListWindow { get; set; } = null!;

    internal TomestoneService TomestoneService { get; private set; } = null!;
    internal FFLogsService FFLogsService { get; private set; } = null!;
    internal CidCache CidCache { get; private set; } = null!;
    internal BlacklistCache BlacklistCache { get; private set; } = null!;
    internal PlayerTrackService PlayerTrackService { get; private set; } = null!;
    internal PartyFinderManager PartyFinderManager { get; private set; } = null!;
    internal PartyListMonitorService PartyListMonitorService { get; private set; } = null!;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Loc.Language = Configuration.Language;  // early; re-applied after client detection below

        TomestoneService = new TomestoneService(this);
        FFLogsService = new FFLogsService(this);
        CidCache = new CidCache();
        BlacklistCache = new BlacklistCache();
        PlayerTrackService = new PlayerTrackService();
        PartyListMonitorService = new PartyListMonitorService(this);

        // Hook registration and addon event subscriptions must run on the main thread.
        await Framework.RunOnFrameworkThread(() =>
        {
            // Detect the client and apply first-launch language / KR defaults (needs game data).
            IsKoreanClient = DetectKoreanClient();
            ApplyClientLanguageDefaults();
            Loc.Language = Configuration.Language;

            PartyFinderManager = new PartyFinderManager(this);
            PFWindowManager.Enable(this, PartyFinderManager);

            MainWindow = new MainWindow(this);
            PFWindow = new PFWindow(this);
            PartyListWindow = new PartyListWindow(this);

            WindowSystem.AddWindow(MainWindow);
            WindowSystem.AddWindow(PFWindow);
            WindowSystem.AddWindow(PartyListWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Passport Check Reborn menu."
            });
            CommandManager.AddHandler(ALTCOMMAND, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open Passport Check Reborn menu."
            });
            CommandManager.AddHandler(PartyListCommandName, new CommandInfo(OnPartyListCommand)
            {
                HelpMessage = "Toggle the Party List Overlay on or off."
            });

            // Register framework update for party list monitoring
            Framework.Update += PartyListMonitorService.OnFrameworkUpdate;

            PluginInterface.UiBuilder.Draw += ManageWindowStates;
            PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        });

        Log.Information($"[PassportCheckerReborn] Plugin loaded.");
    }

    public async ValueTask DisposeAsync()
    {
        // Hook teardown and UI deregistration must run on the main thread.
        await Framework.RunOnFrameworkThread(() =>
        {
            // Unregister framework update for party list monitoring
            Framework.Update -= PartyListMonitorService.OnFrameworkUpdate;

            PluginInterface.UiBuilder.Draw -= ManageWindowStates;
            PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
            PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

            WindowSystem.RemoveAllWindows();

            MainWindow?.Dispose();
            PFWindow?.Dispose();
            PartyListWindow?.Dispose();

            PartyFinderManager?.Dispose();

            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(ALTCOMMAND);
            CommandManager.RemoveHandler(PartyListCommandName);
        });

        PFWindowManager.Disable();

        PartyListMonitorService?.Dispose();
        CidCache?.Dispose();
        BlacklistCache?.Dispose();
        PlayerTrackService?.Dispose();
        TomestoneService?.Dispose();
        FFLogsService?.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        MainWindow.Toggle();
    }

    private void OnPartyListCommand(string command, string args)
    {
        Configuration.ShowPartyListOverlay = !Configuration.ShowPartyListOverlay;
        Configuration.Save();
        ChatGui.Print($"[PassportChecker] {Loc.T(Configuration.ShowPartyListOverlay ? "Party List Overlay shown" : "Party List Overlay hidden")}.");
    }

    public void ToggleMainUi() => MainWindow.Toggle();
    public void ToggleOverlay() => PFWindow.Toggle();

    /// <summary>
    /// Auto-detects the UI language from the client on first launch, and always keeps Tomestone
    /// disabled on the Korean client (Tomestone.gg has no Korean data).
    /// </summary>
    private void ApplyClientLanguageDefaults()
    {
        var changed = false;

        if (!Configuration.LanguageAutoDetected)
        {
            Configuration.Language = IsKoreanClient ? PluginLanguage.Korean : PluginLanguage.English;
            Configuration.LanguageAutoDetected = true;
            changed = true;
        }

        if (IsKoreanClient && Configuration.EnableTomestoneIntegration)
        {
            Configuration.EnableTomestoneIntegration = false;
            changed = true;
        }

        if (changed)
        {
            Configuration.Save();
        }
    }

    /// <summary>
    /// Returns <c>true</c> when running on the Korean client. Dalamud's <see cref="ClientLanguage"/>
    /// enum has no Korean value, so we detect it from game data: on the KR client the "English"
    /// Excel sheet actually returns Korean text, so Hangul in the English ContentFinderCondition
    /// names means this is the KR client.
    /// </summary>
    private static bool DetectKoreanClient()
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<ContentFinderCondition>(ClientLanguage.English);
            var sampled = 0;
            foreach (var row in sheet)
            {
                var name = row.Name.ToString();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (ContainsHangul(name))
                {
                    return true;
                }

                if (++sampled >= 40)
                {
                    break;
                }
            }
        }
        catch (System.Exception ex)
        {
            Log.Warning(ex, "[PassportCheckerReborn] Failed to detect client language.");
        }

        return false;
    }

    private static bool ContainsHangul(string text)
    {
        foreach (var c in text)
        {
            if ((c >= '가' && c <= '힣')   // Hangul syllables
                || (c >= 'ᄀ' && c <= 'ᇿ') // Hangul Jamo
                || (c >= '㄰' && c <= '㆏')) // Hangul compatibility Jamo
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Runs every frame before WindowSystem.Draw to manage auto-open/close
    /// state for windows that depend on external conditions.
    /// </summary>
    private unsafe void ManageWindowStates()
    {
        // PartyListWindow: open when config enabled, and at least one integration enabled
        if (!Configuration.ShowPartyListOverlay
            || (!Configuration.EnableFFLogsIntegrationOverlay && !Configuration.EnableTomestoneIntegration))
        {
            PartyListWindow.IsOpen = false;
            return;
        }

        // Check if party has members via IPartyList (works for regular parties)
        var hasPartyMembers = PartyList.Length > 0;

        // Fallback: check InfoProxyCrossRealm for crossworld parties where IPartyList may be empty
        // (follows the same pattern as ReadyCheckHelper for detecting cross-realm parties)
        if (!hasPartyMembers)
        {
            try
            {
                var cwProxy = InfoProxyCrossRealm.Instance();
                if (cwProxy != null && cwProxy->IsInCrossRealmParty)
                {
                    hasPartyMembers = true;
                }
            }
            catch
            {
                // Ignore failures reading cross-realm state
            }
        }

        if (!hasPartyMembers)
        {
            PartyListWindow.IsOpen = false;
            return;
        }

        // Hide in duty and/or combat based on individual settings
        var hideNow = (Configuration.HidePartyListInDuty && Condition[ConditionFlag.BoundByDuty])
                   || (Configuration.HidePartyListInCombat && Condition[ConditionFlag.InCombat]);
        PartyListWindow.IsOpen = !hideNow;
    }
}
