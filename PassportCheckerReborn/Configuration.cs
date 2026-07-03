using Dalamud.Configuration;
using System;

namespace PassportCheckerReborn;

/// <summary>
/// Determines where the Party List Overlay is positioned relative to the in-game party list.
/// </summary>
public enum PartyListOverlayPosition
{
    Left,
    Right,
    Above,
    Below,
    Unbound,
}

/// <summary>UI display language.</summary>
public enum PluginLanguage
{
    English,
    Korean,
}

/// <summary>
/// Determines which source is consulted first when resolving a party member's
/// name that is not already in the in-memory/persistent PF caches.
/// </summary>
public enum PlayerTrackResolutionPriority
{
    /// <summary>Query the game's adventure plate (CharaCard) first — freshest data,
    /// but fails when the plate is hidden. Falls back to PlayerTrack on failure.</summary>
    CharaCardFirst,

    /// <summary>Use PlayerTrack's stored data first — instant and works for hidden
    /// plates, but may be stale (the player could have renamed/transferred).
    /// Falls back to CharaCard only when PlayerTrack has no record.</summary>
    PlayerTrackFirst,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // ── General ─ Party Finder Detail Optimizations ─────────────────────────
    public bool ShowPartyJobIcons { get; set; } = true;
    public bool PreventAutoClosingOnPartyChanges2 { get; set; } = false;

    // ── General ─ Party Finder List Optimizations ────────────────────────────
    public bool EnableAutomaticRefresh { get; set; } = false;
    public int AutoRefreshIntervalSeconds { get; set; } = 30;
    public bool RightClickPlayerNameForRecruitment3 { get; set; } = false;

    // ── Overlay ───────────────────────────────────────────────────────────────
    public bool ShowMemberInfoOverlay { get; set; } = true;
    public bool OnlyShowOverlayForHighEndDuties { get; set; } = true;
    public bool ShowOverlayOnLeftSide { get; set; } = true;
    public bool ShowResolvedPlayerNames { get; set; } = false;
    public bool EnableFFLogsIntegrationOverlay { get; set; } = false;
    public bool EnableTomestoneIntegration { get; set; } = true;
    public bool ShowPartyListOverlay { get; set; } = false;
    public PartyListOverlayPosition PartyListOverlayPosition { get; set; } = PartyListOverlayPosition.Left;
    public bool HidePartyListInDuty { get; set; } = true;
    public bool HidePartyListInCombat { get; set; } = true;

    // ── Blacklist ─────────────────────────────────────────────────────────────
    public bool EnableBlacklistFeature { get; set; } = true;

    // ── PlayerTrack Integration ───────────────────────────────────────────────
    /// <summary>
    /// When enabled, party-member names that can't be resolved from PF packets/cache
    /// are looked up in the PlayerTrack plugin's local database (read-only).
    /// Safely no-ops if PlayerTrack is not installed / its database is missing.
    /// </summary>
    public bool EnablePlayerTrackIntegration { get; set; } = true;

    /// <summary>
    /// Whether to try the adventure plate (CharaCard) or PlayerTrack first when
    /// resolving unresolved members. See <see cref="PlayerTrackResolutionPriority"/>.
    /// </summary>
    public PlayerTrackResolutionPriority PlayerTrackPriority { get; set; } = PlayerTrackResolutionPriority.CharaCardFirst;

    // ── Name freshness ─────────────────────────────────────────────────────────
    /// <summary>
    /// When a cached name is old, quietly re-checks it against the player's adventure plate
    /// (CharaCard). Throttled so each stale name is retried at most once per cooldown period,
    /// even on failure. Keeps cached names fresh and records renames into the name history.
    /// </summary>
    public bool EnableStaleNameReverification { get; set; } = true;

    /// <summary>A cached name older than this many days is eligible for re-verification.</summary>
    public int StaleNameThresholdDays { get; set; } = 14;

    /// <summary>Minimum hours between re-verification attempts for the same stale name.</summary>
    public int ReverifyCooldownHours { get; set; } = 24;

    /// <summary>
    /// Minimum hours between adventure-plate lookups for a player whose plate is hidden (a
    /// "Private" player). Prevents re-sending a doomed CharaCard request every time you open
    /// their listing, while still eventually noticing if they make their plate public.
    /// </summary>
    public int PrivatePlayerReverifyCooldownHours { get; set; } = 6;

    // ── Localization ────────────────────────────────────────────────────────────
    /// <summary>UI display language.</summary>
    public PluginLanguage Language { get; set; } = PluginLanguage.English;

    /// <summary>Set once after the UI language has been auto-detected from the game client on first
    /// launch. Prevents auto-detection from overriding the user's manual choice thereafter.</summary>
    public bool LanguageAutoDetected { get; set; } = false;

    // ── Tomestone Integration ────────────────────────────────────────────────
    public string TomestoneApiKey { get; set; } = string.Empty;

    // ── FFLogs Integration ───────────────────────────────────────────────────
    public string FFLogsClientId { get; set; } = string.Empty;
    public string FFLogsClientSecret { get; set; } = string.Empty;

    /// <summary>When enabled, the PF member overlay looks up FFLogs data automatically once every
    /// member's name has been resolved, without needing to click the FFLogs button.</summary>
    public bool AutoFetchFFLogsWhenResolved { get; set; } = false;

    public void Save()
    {
        PassportCheckerReborn.PluginInterface.SavePluginConfig(this);
    }
}
