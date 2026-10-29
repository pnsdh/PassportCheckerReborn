using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PassportCheckerReborn.Services;

/// <summary>
/// Provides integration with the
/// <see href="https://www.fflogs.com/api/v2">FFLogs V2 API</see>.
///
/// <para>
/// Authentication uses the OAuth 2.0 Client-Credentials flow:
/// POST https://www.fflogs.com/oauth/token
/// with grant_type=client_credentials + Basic auth (clientId:clientSecret).
/// </para>
///
/// <para>
/// Queries are sent as GraphQL to https://www.fflogs.com/api/v2/client
/// with the Bearer token obtained above.
/// </para>
/// </summary>
public sealed class FFLogsService : IDisposable
{
    private readonly PassportCheckerReborn plugin;
    private readonly HttpClient httpClient;

    // Cancelled on Dispose so any in-flight HTTP stops immediately instead of running the batch to
    // completion (spending rate-limit points and rooting the plugin) after teardown. Every request routes
    // through httpClient.SendAsync with this token.
    private readonly CancellationTokenSource lifetimeCts = new();

    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string ApiUrl = "https://www.fflogs.com/api/v2/client";

    // The token is held with the credentials it was issued for, as one immutable reference, so a reader
    // never sees a token paired with another token's expiry. tokenLock makes concurrent lookups share a fetch.
    private sealed record CachedToken(string Token, DateTime Expiry, string Credentials);

    private volatile CachedToken? cachedToken;
    private readonly SemaphoreSlim tokenLock = new(1, 1);

    // Cache lifetime for batched duty results and zone averages (see dutyResultCache / zoneAverageCache).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    // Latest rate-limit counters, opportunistically captured from every query response (see QueryAsync),
    // so the usage meter reflects real spending without a dedicated request. Held as a single immutable
    // reference so the UI thread's read and the query threads' write are atomic (no torn struct reads).
    private sealed record RateLimitSnapshot(
        int LimitPerHour, double PointsSpentThisHour, int PointsResetInSeconds, DateTime AsOfUtc);

    private volatile RateLimitSnapshot? rateLimitSnapshot;

    public FFLogsService(PassportCheckerReborn plugin)
    {
        this.plugin = plugin;
        httpClient = new HttpClient
        {
            // Cap a hung connection at 30s instead of the 100s default so a stalled request can't pin a
            // lookup task for over a minute.
            Timeout = TimeSpan.FromSeconds(30),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"PassportCheckerReborn/{PassportCheckerReborn.Version}");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Credential testing (called from the ConfigWindow)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to obtain an OAuth token with the supplied credentials.
    /// Returns <c>true</c> when a token is successfully issued. The token is not cached: the credentials
    /// under test may not be the saved ones the lookups use.
    /// </summary>
    public async Task<bool> TestCredentialsAsync(string clientId, string clientSecret)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            return false;
        }

        var token = await FetchTokenAsync(clientId, clientSecret);
        return token is not null;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Token management
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Requests a new OAuth token. Returns null on failure; the caller decides whether to cache it.</summary>
    private async Task<CachedToken?> FetchTokenAsync(string clientId, string clientSecret)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);

            // Basic auth header
            var credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            // Body
            request.Content = new StringContent(
                "grant_type=client_credentials",
                Encoding.UTF8,
                "application/x-www-form-urlencoded");

            using var response = await httpClient.SendAsync(request, lifetimeCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Warning(
                    $"[FFLogsService] Token request failed: {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl) || tokenEl.GetString() is not { } token)
            {
                return null;
            }

            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expEl)
                ? expEl.GetInt32() : 3600;

            PassportCheckerReborn.Log.Information("[FFLogsService] Token obtained successfully.");
            return new CachedToken(
                token,
                DateTime.UtcNow.AddSeconds(expiresIn - 60), // 60-second safety margin
                CredentialKey(clientId, clientSecret));
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during token fetch.");
            return null;
        }
    }

    /// <summary>
    /// Returns a valid Bearer token for the saved credentials, refreshing if necessary. Concurrent lookups
    /// share one token request, and a token is never reused once the saved credentials change.
    /// </summary>
    private async Task<string?> GetTokenAsync()
    {
        var cfg = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.FFLogsClientId) ||
            string.IsNullOrWhiteSpace(cfg.FFLogsClientSecret))
        {
            return null;
        }

        var key = CredentialKey(cfg.FFLogsClientId, cfg.FFLogsClientSecret);
        if (cachedToken is { } cached && cached.Credentials == key && DateTime.UtcNow < cached.Expiry)
        {
            return cached.Token;
        }

        try
        {
            await tokenLock.WaitAsync(lifetimeCts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The plugin is unloading.
            return null;
        }

        try
        {
            // Another lookup may have fetched one while this one waited.
            if (cachedToken is { } fresh && fresh.Credentials == key && DateTime.UtcNow < fresh.Expiry)
            {
                return fresh.Token;
            }

            var fetched = await FetchTokenAsync(cfg.FFLogsClientId, cfg.FFLogsClientSecret);
            if (fetched is not null)
            {
                cachedToken = fetched;
            }

            return fetched?.Token;
        }
        finally
        {
            tokenLock.Release();
        }
    }

    /// <summary>Forgets the cached token if it is still <paramref name="rejected"/>, so the next lookup fetches a new one.</summary>
    private void InvalidateToken(string rejected)
    {
        if (cachedToken?.Token == rejected)
        {
            cachedToken = null;
        }
    }

    private static string CredentialKey(string clientId, string clientSecret)
    {
        return $"{clientId}\n{clientSecret}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GraphQL query helper
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes an arbitrary GraphQL query against the FFLogs V2 API.
    /// Returns the raw JSON response string, or <c>null</c> on failure.
    /// </summary>
    public Task<string?> QueryAsync(string graphqlQuery) => QueryAsync(graphqlQuery, allowTokenRefresh: true);

    private async Task<string?> QueryAsync(string graphqlQuery, bool allowTokenRefresh)
    {
        var token = await GetTokenAsync();
        if (token is null)
        {
            return null;
        }

        try
        {
            // Piggyback the (cheap) rate-limit counters onto every query so the usage meter stays current
            // from ordinary traffic — no dedicated request needed. Skip if the query already asks for them.
            var query = graphqlQuery.Contains("rateLimitData", StringComparison.Ordinal)
                ? graphqlQuery
                : InjectRateLimitField(graphqlQuery);

            var body = System.Text.Json.JsonSerializer.Serialize(new { query });
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, lifetimeCts.Token);

            // The token was invalidated server-side before our local expiry — drop the cached token, fetch a
            // fresh one, and retry once. Without this, every query fails until the ~59-minute local expiry.
            if (response.StatusCode == HttpStatusCode.Unauthorized && allowTokenRefresh)
            {
                PassportCheckerReborn.Log.Information(
                    "[FFLogsService] GraphQL returned 401 — refreshing token and retrying once.");
                InvalidateToken(token);
                return await QueryAsync(graphqlQuery, allowTokenRefresh: false);
            }

            if (!response.IsSuccessStatusCode)
            {
                PassportCheckerReborn.Log.Warning(
                    $"[FFLogsService] GraphQL request failed: {(int)response.StatusCode}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            CaptureRateLimit(json);
            return json;
        }
        catch (OperationCanceledException)
        {
            // Disposed mid-flight or timed out — expected, not worth a warning.
            return null;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during GraphQL request.");
            return null;
        }
    }

    /// <summary>Inserts the rateLimitData root field into a <c>{ … }</c> query so its counters ride along.</summary>
    private static string InjectRateLimitField(string query)
    {
        var brace = query.IndexOf('{');
        return brace < 0
            ? query
            : query.Insert(brace + 1, " rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn }");
    }

    /// <summary>
    /// Extracts rateLimitData from a response (when present) and updates the cached counters. Called for
    /// every query so the usage meter stays current from ordinary traffic. Non-fatal on parse failure.
    /// </summary>
    private void CaptureRateLimit(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataEl)
                && dataEl.TryGetProperty("rateLimitData", out var rl)
                && rl.ValueKind == JsonValueKind.Object)
            {
                var limit = rl.TryGetProperty("limitPerHour", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                var spent = rl.TryGetProperty("pointsSpentThisHour", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                var reset = rl.TryGetProperty("pointsResetIn", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
                rateLimitSnapshot = new RateLimitSnapshot(limit, spent, reset, DateTime.UtcNow);
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to capture rate-limit data.");
        }
    }

    /// <summary>
    /// The most recent rate-limit counters piggybacked onto normal traffic (and when they were captured),
    /// or <c>null</c> if no query has carried them yet. Lets the usage display reflect real spending without
    /// spending a dedicated request.
    /// </summary>
    public (int LimitPerHour, double PointsSpentThisHour, int PointsResetInSeconds, DateTime AsOfUtc)? GetCachedRateLimit()
        => rateLimitSnapshot is { } rl
            ? (rl.LimitPerHour, rl.PointsSpentThisHour, rl.PointsResetInSeconds, rl.AsOfUtc)
            : null;

    /// <summary>
    /// Fetches the current API rate-limit usage: points spent this hour, the hourly limit, and
    /// seconds until the counter resets. Returns <c>null</c> if the query fails (e.g. bad credentials).
    /// </summary>
    public async Task<(int LimitPerHour, double PointsSpentThisHour, int PointsResetInSeconds)?> GetRateLimitAsync()
    {
        var json = await QueryAsync("{ rateLimitData { limitPerHour pointsSpentThisHour pointsResetIn } }");
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var dataEl)
                && dataEl.TryGetProperty("rateLimitData", out var rl)
                && rl.ValueKind == JsonValueKind.Object)
            {
                var limit = rl.TryGetProperty("limitPerHour", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
                var spent = rl.TryGetProperty("pointsSpentThisHour", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                var reset = rl.TryGetProperty("pointsResetIn", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0;
                return (limit, spent, reset);
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse rate-limit response.");
        }

        return null;
    }

    /// <summary>Clears all FFLogs result caches (batched duty results and zone averages).</summary>
    public void ClearCache()
    {
        dutyResultCache.Clear();
        zoneAverageCache.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Encounter-specific data retrieval
    // ─────────────────────────────────────────────────────────────────────────

    // FFLogs difficulty constants
    private const int DifficultyNormal = 100;
    private const int DifficultyHigh = 101;

    /// <summary>
    /// A single duty → FFLogs encounter mapping. Keyed by ContentFinderCondition <see cref="DutyId"/>
    /// (the game's DutyId), which is language-neutral and identical across regions — so this resolves on
    /// every client, including the Korean client whose Excel sheet has no English strings. Multi-phase
    /// fights set <see cref="SecondaryEncounterId"/> (e.g. M4 Savage P1/P2).
    /// </summary>
    /// <param name="HistoricalEncounterIds">
    /// FFLogs re-lists persistent duties (Ultimates) under a NEW encounter ID every expansion, so a
    /// character's kills scatter across several IDs. This holds the SAME fight's encounter IDs from
    /// <b>older</b> expansions (the current-expansion one is <see cref="PrimaryEncounterId"/>). When set,
    /// kills are summed across all of them and the best parse is taken across all of them, so a veteran
    /// who cleared in a previous expansion still shows as cleared. Only Ultimates need this; per-tier
    /// content (Savage/Extreme/Chaotic) is never re-listed, so leave it <c>null</c>.
    /// </param>
    /// <param name="TracksProgression">
    /// Whether FFLogs links wipe-only reports of this duty to the characters in them. It only does so for the
    /// current Savage tier and the current Ultimate; for everything else a report with no kill never shows up
    /// in <c>recentReports</c> (measured 2026-10: 0–1 of 2–39 wipe-only reports per zone linked, against all of
    /// them for zones 73 and 76), so a progression read costs ~9x a rankings read and almost never finds
    /// anything. Move this flag to the new content each tier.
    /// </param>
    private readonly record struct DutyEntry(
        uint DutyId, string Name, int ZoneId, int PrimaryEncounterId, int? SecondaryEncounterId, int Difficulty,
        int[]? HistoricalEncounterIds = null, bool TracksProgression = false);

    /// <summary>
    /// Single source of truth for the FFLogs duty mapping — update this ONE list each content tier.
    /// <para>DutyId: the game's ContentFinderCondition RowId (read it from the "[PCR:Refresh] … DutyId="
    /// log line, or xivapi/ffxiv-datamining). ZoneId/EncounterId: from FFLogs. Name: shown in the manual
    /// duty dropdown and keeps the list readable.</para>
    /// </summary>
    private static readonly DutyEntry[] Duties =
    {
        // ── Savage — AAC Light-heavyweight ──
        new(986,  "AAC Light-heavyweight M1 (Savage)", 62, 93,  null, DifficultyHigh),
        new(988,  "AAC Light-heavyweight M2 (Savage)", 62, 94,  null, DifficultyHigh),
        new(990,  "AAC Light-heavyweight M3 (Savage)", 62, 95,  null, DifficultyHigh),
        new(992,  "AAC Light-heavyweight M4 (Savage)", 62, 96,  null, DifficultyHigh),

        // ── Savage — AAC Cruiserweight ──
        new(1020, "AAC Cruiserweight M1 (Savage)",     68, 97,  null, DifficultyHigh),
        new(1022, "AAC Cruiserweight M2 (Savage)",     68, 98,  null, DifficultyHigh),
        new(1024, "AAC Cruiserweight M3 (Savage)",     68, 99,  null, DifficultyHigh),
        new(1026, "AAC Cruiserweight M4 (Savage)",     68, 100, null, DifficultyHigh),

        // ── Savage — AAC Heavyweight (M4 is two phases) — current tier, so progression is read ──
        new(1069, "AAC Heavyweight M1 (Savage)",       73, 101, null, DifficultyHigh, TracksProgression: true),
        new(1071, "AAC Heavyweight M2 (Savage)",       73, 102, null, DifficultyHigh, TracksProgression: true),
        new(1073, "AAC Heavyweight M3 (Savage)",       73, 103, null, DifficultyHigh, TracksProgression: true),
        new(1075, "AAC Heavyweight M4 (Savage)",       73, 104, 105,  DifficultyHigh, TracksProgression: true),

        // ── Chaotic ──
        new(1010, "The Cloud of Darkness (Chaotic)",   66, 2061, null, DifficultyNormal),

        // ── Unreal (rotating — keep only the currently-active one) ──
        new(1118, "Shinryu's Domain (Unreal)",         64, 3013, null, DifficultyNormal),

        // ── Extreme ──
        new(833,  "Worqor Lar Dor (Extreme)",                58, 1071, null, DifficultyNormal),
        new(996,  "Everkeep (Extreme)",                      58, 1072, null, DifficultyNormal),
        new(1017, "The Minstrel's Ballad: Sphene's Burden",  58, 1078, null, DifficultyNormal),
        new(1031, "Recollection (Extreme)",                  67, 1080, null, DifficultyNormal),
        new(1062, "The Minstrel's Ballad: Necron's Embrace", 67, 1081, null, DifficultyNormal),
        new(1044, "The Windward Wilds (Extreme)",            67, 1082, null, DifficultyNormal),
        new(1077, "Hell on Rails (Extreme)",                 72, 1083, null, DifficultyNormal),
        new(1116, "The Unmaking (Extreme)",                  72, 1084, null, DifficultyNormal),

        // ── Ultimate ──
        // The last arg is HistoricalEncounterIds: the SAME fight's encounter IDs from OLDER expansions
        // (FFLogs re-lists each Ultimate under a fresh ID every expansion). Kills are summed and the best
        // parse is taken across [primary + historical], so a clear from any expansion counts. The primary
        // here is the current (Dawntrail) listing; historical are the prior-expansion listings in age order.
        //   Zone map: 19=Ultimates(Stormblood) 30=Ultimates(Shadowbringers) 43=Ultimates(Endwalker) 59=Ultimates(Dawntrail)
        // FRU (7.1) and Dancing Mad (7.5) were introduced IN Dawntrail, so they have no older listings.
        // Historical arrays are ordered newest→oldest expansion: [EW(43), ShB(30), SB(19)].
        new(280,  "The Unending Coil of Bahamut (Ultimate)", 59, 1073, null, DifficultyNormal, [1060, 1047, 1039]),
        new(539,  "The Weapon's Refrain (Ultimate)",         59, 1074, null, DifficultyNormal, [1061, 1048, 1042]),
        new(694,  "The Epic of Alexander (Ultimate)",        59, 1075, null, DifficultyNormal, [1062, 1050]),
        new(788,  "Dragonsong's Reprise (Ultimate)",         59, 1076, null, DifficultyNormal, [1065]),
        new(908,  "The Omega Protocol (Ultimate)",           59, 1077, null, DifficultyNormal, [1068]),
        new(1006, "Futures Rewritten (Ultimate)",            65, 1079, null, DifficultyNormal),
        new(1094, "Dancing Mad (Ultimate)",                  76, 1085, null, DifficultyNormal, TracksProgression: true),
    };

    private static readonly Dictionary<uint, DutyEntry> DutyById;
    private static readonly Dictionary<string, DutyEntry> DutyByName;
    private static readonly Dictionary<int, DutyEntry> DutyByEncounterId;

    static FFLogsService()
    {
        DutyById = new Dictionary<uint, DutyEntry>();
        DutyByName = new Dictionary<string, DutyEntry>(StringComparer.OrdinalIgnoreCase);
        DutyByEncounterId = new Dictionary<int, DutyEntry>();
        foreach (var d in Duties)
        {
            DutyById[d.DutyId] = d;
            DutyByName[d.Name] = d;
            DutyByEncounterId[d.PrimaryEncounterId] = d;
            if (d.SecondaryEncounterId is { } secondary)
            {
                DutyByEncounterId[secondary] = d;
            }

            if (d.HistoricalEncounterIds is { } historical)
            {
                foreach (var h in historical)
                {
                    DutyByEncounterId[h] = d;
                }
            }
        }
    }

    /// <summary>Returns the FFLogs difficulty level for the given encounter ID, or <c>null</c> if unknown.</summary>
    public static int? GetDifficultyForEncounter(int encounterId)
        => DutyByEncounterId.TryGetValue(encounterId, out var d) ? d.Difficulty : null;

    /// <summary>Returns the FFLogs zone ID for the given encounter ID, or <c>null</c> if unknown.</summary>
    public static int? GetZoneIdForEncounter(int encounterId)
        => DutyByEncounterId.TryGetValue(encounterId, out var d) ? d.ZoneId : null;

    /// <summary>Returns the FFLogs zone ID for the given FFXIV duty name, or <c>null</c> if not mapped.</summary>
    public static int? GetZoneIdForDuty(string? dutyName)
        => dutyName != null && DutyByName.TryGetValue(dutyName, out var d) ? d.ZoneId : null;

    /// <summary>Returns the primary FFLogs encounter ID for the given FFXIV duty name, or <c>null</c>.</summary>
    public static int? GetEncounterIdForDuty(string? dutyName)
        => GetEncounterIdsForDuty(dutyName)?.PrimaryEncounterId;

    /// <summary>
    /// Returns the FFLogs encounter IDs for the given FFXIV duty name (with a multi-phase secondary),
    /// or <c>null</c> if not mapped.
    /// </summary>
    public static (int PrimaryEncounterId, int? SecondaryEncounterId)? GetEncounterIdsForDuty(string? dutyName)
        => dutyName != null && DutyByName.TryGetValue(dutyName, out var d)
            ? (d.PrimaryEncounterId, d.SecondaryEncounterId)
            : null;

    /// <summary>
    /// Returns the FFLogs encounter IDs for the given ContentFinderCondition RowId (the game's DutyId),
    /// falling back to <paramref name="fallbackDutyName"/> when the id isn't mapped (e.g. a manual
    /// dropdown selection passes DutyId 0). RowId keying is language-neutral, so this works on KR.
    /// </summary>
    public static (int PrimaryEncounterId, int? SecondaryEncounterId)? GetEncounterIdsForDuty(
        uint dutyId,
        string? fallbackDutyName = null)
    {
        if (dutyId > 0 && DutyById.TryGetValue(dutyId, out var d))
        {
            return (d.PrimaryEncounterId, d.SecondaryEncounterId);
        }

        return GetEncounterIdsForDuty(fallbackDutyName);
    }

    /// <summary>
    /// Resolves the full <see cref="DutyEntry"/> for a duty id (falling back to the localized-name lookup),
    /// or <c>null</c> when the duty isn't mapped. Used to reach <see cref="DutyEntry.HistoricalEncounterIds"/>.
    /// </summary>
    private static DutyEntry? GetDutyEntry(uint dutyId, string? fallbackDutyName)
    {
        if (dutyId > 0 && DutyById.TryGetValue(dutyId, out var byId))
        {
            return byId;
        }

        if (fallbackDutyName != null && DutyByName.TryGetValue(fallbackDutyName, out var byName))
        {
            return byName;
        }

        return null;
    }

    /// <summary>
    /// Returns every mapped duty as (DutyId, English name), for building the manual duty dropdown.
    /// The dropdown resolves each DutyId to a localised display name from the game, so the English
    /// name here is only the internal key.
    /// </summary>
    public static IReadOnlyList<(uint DutyId, string Name)> GetSupportedDuties()
    {
        var list = new List<(uint DutyId, string Name)>(Duties.Length);
        foreach (var d in Duties)
        {
            list.Add((d.DutyId, d.Name));
        }

        return list;
    }

    /// <summary>
    /// Fetches parse/kill data for all members for a whole DUTY (identified by its ContentFinderCondition
    /// RowId, falling back to <paramref name="fallbackDutyName"/>). Transparently handles multi-phase fights
    /// (P1/P2) and Ultimates that FFLogs re-lists per expansion: for a duty with
    /// <see cref="DutyEntry.HistoricalEncounterIds"/>, kills are summed and the best parse is taken across
    /// every expansion's listing, so a clear from any expansion still counts.
    /// Returns an empty dictionary when the duty isn't mapped (caller should fall back to a zone average).
    /// <para><paramref name="onMemberUpdated"/>, if supplied, is invoked (member index, result) as each
    /// member's data becomes ready — the fast kill/parse data fires first, then the slower progression fills
    /// in — so the UI can render progressively instead of waiting for the whole batch. It may be called from a
    /// background thread and more than once per member.</para>
    /// </summary>
    public async Task<Dictionary<int, EncounterParseResult>> GetDutyEncounterDataForAllAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        uint dutyId,
        string? fallbackDutyName,
        Action<int, EncounterParseResult>? onMemberUpdated = null)
    {
        if (GetDutyEntry(dutyId, fallbackDutyName) is not { } entry)
        {
            return new Dictionary<int, EncounterParseResult>();
        }

        var dutyKey = dutyId > 0 ? $"d{dutyId}" : $"n{fallbackDutyName}";
        var results = new Dictionary<int, EncounterParseResult>();

        // Serve cache hits directly; only the misses (with a resolvable name) hit the API.
        var missOriginalIndex = new List<int>();
        var missMembers = new List<(string Name, string World, string JobAbbreviation)>();
        for (var i = 0; i < members.Count; i++)
        {
            if (string.IsNullOrEmpty(members[i].Name))
            {
                continue;
            }

            if (TryGetCachedResult(members[i], dutyKey, out var cached))
            {
                // The cache is keyed by player, not job, so the current-job parse is re-derived for the job
                // the member is on now (no extra request).
                cached = WithCurrentJob(cached, members[i].JobAbbreviation);
                results[i] = cached;
                onMemberUpdated?.Invoke(i, cached);
            }
            else
            {
                missOriginalIndex.Add(i);
                missMembers.Add(members[i]);
            }
        }

        if (missMembers.Count > 0)
        {
            // Translate the miss-subset's local indices back to the caller's original indices for the callback.
            var localOnUpdated = onMemberUpdated is null
                ? (Action<int, EncounterParseResult>?)null
                : (localIndex, result) => onMemberUpdated(missOriginalIndex[localIndex], result);

            var fresh = await ComputeDutyDataAsync(missMembers, entry, localOnUpdated);
            foreach (var (localIndex, result) in fresh)
            {
                var originalIndex = missOriginalIndex[localIndex];
                results[originalIndex] = result;

                // Never cache a failed lookup — leave it out so the next refresh retries.
                if (!result.FetchFailed)
                {
                    StoreCachedResult(missMembers[localIndex], dutyKey, result);
                }
            }
        }

        return results;
    }

    /// <summary>A failed lookup: retryable and never cached, shown as "lookup failed".</summary>
    private static EncounterParseResult FailedResult() => new(false, true, 0, null, null) { FetchFailed = true };

    /// <summary>No data on this encounter (no clear, no progression found).</summary>
    private static EncounterParseResult NoDataResult() => new(false, true, 0, null, null);

    /// <summary>
    /// Does the real API work for a cache-miss subset of members (indexed 0..n-1), in at most two requests
    /// whatever the duty:
    /// <list type="number">
    /// <item>One rankings request covering every member and every listing of the fight: the fight itself, or
    /// P1 + P2 for a two-phase fight, plus for Ultimates the same fight's older-expansion listings (folded in).
    /// Characters FFLogs doesn't know come back as <see cref="EncounterParseResult.CharacterNotFound"/>.</item>
    /// <item>One progression request for the members without a clear, each reading their recent reports
    /// once for every phase they haven't cleared — only for duties that <see cref="DutyEntry.TracksProgression"/>.</item>
    /// </list>
    /// Clears/parses are published as soon as the rankings arrive; the rest once progression resolves.
    /// </summary>
    private async Task<Dictionary<int, EncounterParseResult>> ComputeDutyDataAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        DutyEntry entry,
        Action<int, EncounterParseResult>? onUpdated = null)
    {
        var results = new Dictionary<int, EncounterParseResult>();

        void Publish(int i, EncounterParseResult r)
        {
            results[i] = r;
            onUpdated?.Invoke(i, r);
        }

        int[] phaseIds = entry.SecondaryEncounterId is { } secondary
            ? [entry.PrimaryEncounterId, secondary]
            : [entry.PrimaryEncounterId];
        var historical = entry.HistoricalEncounterIds ?? [];

        var batch = await QueryEncounterRankingsBatchAsync(members, [.. phaseIds, .. historical]);
        if (batch is null)
        {
            // The request itself failed: everyone with a name is retryable, nothing is cached.
            for (var i = 0; i < members.Count; i++)
            {
                if (!string.IsNullOrEmpty(members[i].Name))
                {
                    Publish(i, FailedResult());
                }
            }

            return results;
        }

        // Each member's per-phase results, and the phases still needing progression (no clear).
        var phaseResults = new Dictionary<int, EncounterParseResult[]>();
        var needsProgression = new Dictionary<int, List<int>>();

        for (var i = 0; i < members.Count; i++)
        {
            if (string.IsNullOrEmpty(members[i].Name))
            {
                continue;
            }

            if (batch.Failed.Contains(i))
            {
                Publish(i, FailedResult());
                continue;
            }

            if (batch.NotFound.Contains(i))
            {
                // Known answer (cached): FFLogs has no such character, so there is no progression to look for.
                Publish(i, NoDataResult() with { CharacterNotFound = true });
                continue;
            }

            if (batch.Hidden.Contains(i))
            {
                // Known answer (cached): whether they cleared can't be told, and wipes read from their reports
                // would pass a clear off as progression, so no progression read either.
                Publish(i, NoDataResult() with { RankingsHidden = true });
                continue;
            }

            if (!batch.Queried.Contains(i))
            {
                // The world couldn't be mapped to an FFLogs server.
                Publish(i, NoDataResult());
                continue;
            }

            batch.Data.TryGetValue(i, out var perEncounter);
            var phases = new EncounterParseResult[phaseIds.Length];
            for (var p = 0; p < phaseIds.Length; p++)
            {
                phases[p] = perEncounter != null && perEncounter.TryGetValue(phaseIds[p], out var d)
                    ? BuildSinglePhaseResult(d)
                    : NoDataResult();
            }

            // Ultimates (single-phase): a clear or parse from any expansion's listing counts.
            if (perEncounter != null)
            {
                foreach (var histId in historical)
                {
                    if (perEncounter.TryGetValue(histId, out var hist))
                    {
                        phases[0] = MergeHistorical(phases[0], hist);
                    }
                }
            }

            phaseResults[i] = phases;

            // FFLogs doesn't link this duty's wipe-only reports to characters (see TracksProgression), so there
            // is no progression to find: publish what the rankings showed, marked so the UI can say why.
            if (!entry.TracksProgression)
            {
                var combined = CombinePhases(phases);
                Publish(i, combined.TotalKills > 0 ? combined : combined with { ProgressionNotTracked = true });
                continue;
            }

            // Progression is measured on the live listing of each phase without a clear (in any expansion).
            var uncleared = new List<int>();
            for (var p = 0; p < phaseIds.Length; p++)
            {
                if (phases[p].TotalKills == 0)
                {
                    uncleared.Add(phaseIds[p]);
                }
            }

            if (uncleared.Count > 0)
            {
                needsProgression[i] = uncleared;
            }
            else
            {
                Publish(i, CombinePhases(phases));
            }
        }

        if (needsProgression.Count > 0)
        {
            var (progression, progressionFailed) = await FetchProgressionDataAsync(members, needsProgression);
            foreach (var (i, encounters) in needsProgression)
            {
                var phases = phaseResults[i];
                if (progressionFailed.Contains(i))
                {
                    // Keep what the rankings already showed (e.g. a two-phase fight's P1 clear); the phases
                    // whose progression couldn't be read show as failed, and the whole result is retried.
                    for (var p = 0; p < phaseIds.Length; p++)
                    {
                        if (encounters.Contains(phaseIds[p]))
                        {
                            phases[p] = FailedResult();
                        }
                    }

                    Publish(i, CombinePhases(phases) with { FetchFailed = true });
                    continue;
                }

                for (var p = 0; p < phaseIds.Length; p++)
                {
                    if (encounters.Contains(phaseIds[p]) && progression.TryGetValue((i, phaseIds[p]), out var prog))
                    {
                        phases[p] = prog;
                    }
                }

                Publish(i, CombinePhases(phases));
            }
        }

        // No zone-average fallback for mapped duties on purpose: for a specific encounter we only want that
        // encounter's data — a current-tier average was more confusing than helpful, and skipping it also
        // avoids spending points on it. (The unmapped-duty path still shows a zone average as its only signal.)
        return results;
    }

    /// <summary>
    /// A single fight's result as is, or a two-phase fight's P1 and P2 results combined: full clears are the
    /// smaller kill count, parse and best-parse job come from the most relevant phase (P2, else P1), and each
    /// phase's own result is kept so the overlay can show both.
    /// </summary>
    private static EncounterParseResult CombinePhases(EncounterParseResult[] phases)
    {
        if (phases.Length == 1)
        {
            return phases[0];
        }

        var phase1 = phases[0];
        var phase2 = phases[1];
        var bestPhase = phase2.HasData && phase2.TotalKills > 0 ? phase2
                      : phase1.HasData && phase1.TotalKills > 0 ? phase1
                      : phase2;

        return new EncounterParseResult(phase1.HasData || phase2.HasData, true,
            Math.Min(phase1.TotalKills, phase2.TotalKills), bestPhase.BestParse, null)
        {
            Phase1BestParse = phase1.BestParse,
            Phase2BestParse = phase2.BestParse,
            Phase2LowestBossHpPct = phase2.LowestBossHpPct,
            Phase1TotalKills = phase1.TotalKills,
            Phase2TotalKills = phase2.TotalKills,
            CurrentJobBestParse = MaxNullable(phase1.CurrentJobBestParse, phase2.CurrentJobBestParse),
            BestParseBySpec = MergeSpecBests(phase1.BestParseBySpec, phase2.BestParseBySpec),
            BestParseSpec = bestPhase.BestParseSpec,
            BestParseJobAbbreviation = bestPhase.BestParseJobAbbreviation,
            BestParseJobIconId = bestPhase.BestParseJobIconId,
            // Full per-phase results so the overlay can render P1 and P2 each as its own single-phase cell.
            Phase1Result = phase1,
            Phase2Result = phase2,
        };
    }

    /// <summary>
    /// What one rankings batch returned: memberIndex → (encounterId → rank data), which members were actually
    /// queried (their world maps to an FFLogs server), which ones FFLogs has no character for, which ones have
    /// hidden their rankings, and which ones hit a transient per-member error (retryable).
    /// </summary>
    private sealed record RankingsBatch(
        Dictionary<int, Dictionary<int, EncounterRankData>> Data,
        HashSet<int> Queried,
        HashSet<int> NotFound,
        HashSet<int> Hidden,
        HashSet<int> Failed);

    /// <summary>
    /// One-request batch of encounterRankings for every (member × encounter) pair via GraphQL aliases, so a
    /// single HTTP round-trip covers all members and all encounter IDs. Returns <c>null</c> when the request
    /// itself failed (so callers can distinguish a failure from "everyone has no data"). Progression is NOT
    /// fetched here.
    /// </summary>
    private async Task<RankingsBatch?> QueryEncounterRankingsBatchAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        IReadOnlyList<int> encounterIds)
    {
        var batch = new RankingsBatch([], [], [], [], []);
        if (encounterIds.Count == 0)
        {
            return batch;
        }

        var memberParts = new List<string>();
        for (var i = 0; i < members.Count; i++)
        {
            var (name, world, _) = members[i];
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var serverInfo = GetFFLogsServer(world);
            if (serverInfo is null)
            {
                continue;
            }

            var (slug, region) = serverInfo.Value;
            var encounterParts = new List<string>(encounterIds.Count);
            foreach (var encId in encounterIds)
            {
                var difficultyParam = GetDifficultyForEncounter(encId) is { } diff ? $", difficulty: {diff}" : string.Empty;
                encounterParts.Add($"e{encId}: encounterRankings(encounterID: {encId}{difficultyParam})");
            }

            batch.Queried.Add(i);
            memberParts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ {string.Join(" ", encounterParts)} }}");
        }

        if (memberParts.Count == 0)
        {
            return batch;
        }

        var fullQuery = $"{{ characterData {{ {string.Join(" ", memberParts)} }} }}";
        var json = await QueryAsync(fullQuery);
        if (json is null)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                return null;
            }

            var transientErrored = GetTransientlyErroredAliases(doc.RootElement);
            var anyErrored = GetErroredAliases(doc.RootElement);

            foreach (var i in batch.Queried)
            {
                if (transientErrored.Contains(i))
                {
                    batch.Failed.Add(i);
                    continue;
                }

                if (!charDataEl.TryGetProperty($"p{i}", out var charEl) || charEl.ValueKind == JsonValueKind.Null)
                {
                    // FFLogs answers a name/server it has no character for with a plain null and no error entry.
                    // A null that comes with an error is something else: treat it as plain "no data".
                    if (!anyErrored.Contains(i))
                    {
                        batch.NotFound.Add(i);
                    }

                    continue;
                }

                var currentJobSpec = GetSpecForJob(members[i].JobAbbreviation);
                var perEncounter = new Dictionary<int, EncounterRankData>();
                var hidden = false;
                foreach (var encId in encounterIds)
                {
                    if (charEl.TryGetProperty($"e{encId}", out var rankingsEl) && rankingsEl.ValueKind != JsonValueKind.Null)
                    {
                        if (IsRankingsHidden(rankingsEl))
                        {
                            hidden = true;
                            break;
                        }

                        perEncounter[encId] = ParseEncounterRank(rankingsEl, currentJobSpec);
                    }
                }

                if (hidden)
                {
                    batch.Hidden.Add(i);
                    continue;
                }

                if (perEncounter.Count > 0)
                {
                    batch.Data[i] = perEncounter;
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse batched encounter rankings.");

            // Whoever wasn't read yet can't be told apart from "no data": retry them.
            foreach (var i in batch.Queried)
            {
                if (!batch.Data.ContainsKey(i) && !batch.NotFound.Contains(i))
                {
                    batch.Failed.Add(i);
                }
            }
        }

        return batch;
    }

    /// <summary>
    /// Whether an encounterRankings blob is FFLogs' refusal for a character whose rankings are private. It
    /// comes inside the JSON scalar as <c>{"error": "You do not have permission to see this character's
    /// rankings."}</c>, not as a GraphQL error, so it would otherwise parse as zero kills.
    /// </summary>
    private static bool IsRankingsHidden(JsonElement rankingsEl)
        => rankingsEl.ValueKind == JsonValueKind.Object
           && rankingsEl.TryGetProperty("error", out var errEl)
           && errEl.ValueKind == JsonValueKind.String
           && errEl.GetString() is { } message
           && message.Contains("permission", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extracts kills + best/current-job parse from one encounterRankings JSON blob.</summary>
    private static EncounterRankData ParseEncounterRank(JsonElement rankingsEl, string? currentJobSpec)
    {
        var totalKills = 0;
        if (rankingsEl.TryGetProperty("totalKills", out var tkEl) && tkEl.ValueKind == JsonValueKind.Number)
        {
            totalKills = tkEl.GetInt32();
        }

        double? bestParse = null;
        string? bestParseSpec = null;
        double? currentJobBestParse = null;
        var bestBySpec = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (rankingsEl.TryGetProperty("ranks", out var ranksEl) && ranksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var rank in ranksEl.EnumerateArray())
            {
                if (!rank.TryGetProperty("rankPercent", out var pctEl) || pctEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var pct = pctEl.GetDouble();
                var spec = rank.TryGetProperty("spec", out var specEl) ? specEl.GetString() : null;
                RecordSpecBest(bestBySpec, spec, pct);

                if (!bestParse.HasValue || pct > bestParse.Value)
                {
                    bestParse = pct;
                    bestParseSpec = spec;
                }

                if (currentJobSpec != null && spec != null &&
                    string.Equals(spec, currentJobSpec, StringComparison.OrdinalIgnoreCase) &&
                    (!currentJobBestParse.HasValue || pct > currentJobBestParse.Value))
                {
                    currentJobBestParse = pct;
                }
            }
        }

        return new EncounterRankData(totalKills, bestParse, bestParseSpec, currentJobBestParse, bestBySpec);
    }

    /// <summary>Keeps the highest parse seen per FFLogs spec.</summary>
    private static void RecordSpecBest(Dictionary<string, double> bestBySpec, string? spec, double pct)
    {
        if (spec != null && (!bestBySpec.TryGetValue(spec, out var prev) || pct > prev))
        {
            bestBySpec[spec] = pct;
        }
    }

    /// <summary>Per-spec bests of two results combined, keeping the higher parse for each spec.</summary>
    private static IReadOnlyDictionary<string, double>? MergeSpecBests(
        IReadOnlyDictionary<string, double>? a, IReadOnlyDictionary<string, double>? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }

        var merged = new Dictionary<string, double>(a, StringComparer.OrdinalIgnoreCase);
        foreach (var (spec, pct) in b)
        {
            RecordSpecBest(merged, spec, pct);
        }

        return merged;
    }

    /// <summary>
    /// <paramref name="result"/> with its current-job parse taken for <paramref name="jobAbbreviation"/>, from
    /// the per-spec bests it carries. Results without per-spec data are returned unchanged.
    /// </summary>
    private static EncounterParseResult WithCurrentJob(EncounterParseResult result, string jobAbbreviation)
    {
        if (result.BestParseBySpec is not { } bySpec)
        {
            return result;
        }

        var spec = GetSpecForJob(jobAbbreviation);
        return result with
        {
            CurrentJobBestParse = spec != null && bySpec.TryGetValue(spec, out var pct) ? pct : null,
        };
    }

    /// <summary>Builds a single-phase <see cref="EncounterParseResult"/> from one encounter's raw rank data.</summary>
    private static EncounterParseResult BuildSinglePhaseResult(EncounterRankData d)
        => new(d.TotalKills > 0, true, d.TotalKills, d.BestParse, null)
        {
            CurrentJobBestParse = d.CurrentJobBestParse,
            BestParseBySpec = d.BestParseBySpec,
            BestParseSpec = d.BestParseSpec,
            BestParseJobAbbreviation = GetJobAbbrevForSpec(d.BestParseSpec),
            BestParseJobIconId = GetJobIconIdForSpec(d.BestParseSpec),
        };

    /// <summary>
    /// Folds an older-expansion listing's rank data into the current result: kills summed, best/current-job
    /// parse taken as the max across expansions, best-parse job following whichever expansion parsed higher.
    /// </summary>
    private static EncounterParseResult MergeHistorical(EncounterParseResult current, EncounterRankData hist)
    {
        var combinedKills = current.TotalKills + hist.TotalKills;
        var histParseWins = (hist.BestParse ?? double.NegativeInfinity) > (current.BestParse ?? double.NegativeInfinity);

        return current with
        {
            HasData = current.HasData || hist.TotalKills > 0 || combinedKills > 0,
            TotalKills = combinedKills,
            BestParse = MaxNullable(current.BestParse, hist.BestParse),
            CurrentJobBestParse = MaxNullable(current.CurrentJobBestParse, hist.CurrentJobBestParse),
            BestParseBySpec = MergeSpecBests(current.BestParseBySpec, hist.BestParseBySpec),
            BestParseSpec = histParseWins ? hist.BestParseSpec : current.BestParseSpec,
            BestParseJobAbbreviation = histParseWins ? GetJobAbbrevForSpec(hist.BestParseSpec) : current.BestParseJobAbbreviation,
            BestParseJobIconId = histParseWins ? GetJobIconIdForSpec(hist.BestParseSpec) : current.BestParseJobIconId,
        };
    }

    private static double? MaxNullable(double? a, double? b)
        => a.HasValue && b.HasValue ? Math.Max(a.Value, b.Value) : a ?? b;

    /// <summary>
    /// Batched zone-average lookup for the unmapped-duty fallback: one request fetches every member's overall
    /// best performance average. Cached per character for <see cref="CacheTtl"/>.
    /// </summary>
    public async Task<Dictionary<int, EncounterParseResult>> GetZoneAveragesForAllAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members)
    {
        var indices = new List<int>(members.Count);
        for (var i = 0; i < members.Count; i++)
        {
            indices.Add(i);
        }

        var (averages, failed) = await QueryZoneAveragesAsync(members, indices);
        var results = new Dictionary<int, EncounterParseResult>();
        foreach (var (i, avg) in averages)
        {
            results[i] = avg.HasValue
                ? new EncounterParseResult(true, false, 0, avg.Value, null)
                : new EncounterParseResult(false, false, 0, null, null);
        }

        // A failed request is not "no data": mark it so the cell reads "lookup failed" and a retry covers it.
        foreach (var i in failed)
        {
            results[i] = new EncounterParseResult(false, false, 0, null, null) { FetchFailed = true };
        }

        return results;
    }

    /// <summary>
    /// Batched <c>zoneRankings</c> lookup for the given member indices in a single request, returning
    /// memberIndex → best (or median) performance average, plus the members whose lookup failed (request or
    /// response error), which are left out of the averages. Results are cached per character for
    /// <see cref="CacheTtl"/>, so cached members are served without hitting the API.
    /// </summary>
    private async Task<(Dictionary<int, double?> Averages, List<int> Failed)> QueryZoneAveragesAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        IReadOnlyList<int> indices)
    {
        var result = new Dictionary<int, double?>();
        var parts = new List<string>();
        var valid = new List<int>();

        foreach (var i in indices)
        {
            var (name, world, _) = members[i];
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var cacheKey = $"{name}@{world}";
            if (zoneAverageCache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.Expiry)
            {
                result[i] = cached.Average;
                continue;
            }

            var serverInfo = GetFFLogsServer(world);
            if (serverInfo is null)
            {
                continue;
            }

            var (slug, region) = serverInfo.Value;
            valid.Add(i);
            parts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ zoneRankings }}");
        }

        if (parts.Count == 0)
        {
            return (result, []);
        }

        var query = $"{{ characterData {{ {string.Join(" ", parts)} }} }}";
        var json = await QueryAsync(query);
        if (json is null)
        {
            return (result, valid);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                return (result, valid);
            }

            foreach (var i in valid)
            {
                double? avg = null;
                if (charDataEl.TryGetProperty($"p{i}", out var charEl) && charEl.ValueKind != JsonValueKind.Null &&
                    charEl.TryGetProperty("zoneRankings", out var zr) && zr.ValueKind == JsonValueKind.Object)
                {
                    if (zr.TryGetProperty("bestPerformanceAverage", out var b) && b.ValueKind == JsonValueKind.Number)
                    {
                        avg = b.GetDouble();
                    }
                    else if (zr.TryGetProperty("medianPerformanceAverage", out var m) && m.ValueKind == JsonValueKind.Number)
                    {
                        avg = m.GetDouble();
                    }
                }

                result[i] = avg;
                zoneAverageCache[$"{members[i].Name}@{members[i].World}"] = (avg, DateTime.UtcNow + CacheTtl);
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse batched zone averages.");
        }

        PruneExpiredCachesIfLarge();
        return (result, valid.FindAll(i => !result.ContainsKey(i)));
    }

    // ── Batch-path caching (keyed per character) ────────────────────────────
    private readonly ConcurrentDictionary<string, (EncounterParseResult Result, DateTime Expiry)> dutyResultCache = new();
    private readonly ConcurrentDictionary<string, (double? Average, DateTime Expiry)> zoneAverageCache = new();

    // Above this many entries, a write sweeps out expired entries so a long session scanning many distinct
    // players can't grow either cache without bound. Entries expire after CacheTtl anyway, so the live set is
    // small; this just reclaims the accumulated dead keys.
    private const int CacheSoftCap = 512;

    /// <summary>Reclaims expired entries from both batch caches once either exceeds <see cref="CacheSoftCap"/>.</summary>
    private void PruneExpiredCachesIfLarge()
    {
        if (dutyResultCache.Count <= CacheSoftCap && zoneAverageCache.Count <= CacheSoftCap)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var kv in dutyResultCache)
        {
            if (now >= kv.Value.Expiry)
            {
                dutyResultCache.TryRemove(kv.Key, out _);
            }
        }

        foreach (var kv in zoneAverageCache)
        {
            if (now >= kv.Value.Expiry)
            {
                zoneAverageCache.TryRemove(kv.Key, out _);
            }
        }
    }

    private static string PlayerKeyOf(in (string Name, string World, string JobAbbreviation) m)
        => $"{m.Name}@{m.World}";

    private bool TryGetCachedResult(
        in (string Name, string World, string JobAbbreviation) member, string dutyKey, out EncounterParseResult result)
    {
        if (dutyResultCache.TryGetValue($"{PlayerKeyOf(member)}#{dutyKey}", out var entry) && DateTime.UtcNow < entry.Expiry)
        {
            result = entry.Result;
            return true;
        }

        result = default!;
        return false;
    }

    private void StoreCachedResult(
        in (string Name, string World, string JobAbbreviation) member, string dutyKey, EncounterParseResult result)
    {
        dutyResultCache[$"{PlayerKeyOf(member)}#{dutyKey}"] = (result, DateTime.UtcNow + CacheTtl);
        PruneExpiredCachesIfLarge();
    }

    /// <summary>Kills + parse extracted from a single encounterRankings blob (one member, one encounter).</summary>
    private readonly record struct EncounterRankData(
        int TotalKills, double? BestParse, string? BestParseSpec, double? CurrentJobBestParse,
        IReadOnlyDictionary<string, double>? BestParseBySpec);

    // How many recent reports per player to scan for progression pulls. Larger = catches older prog at
    // the cost of more API points. FFLogs has no "furthest pull ever" field, so this is the practical window.
    private const int ProgressionReportLimit = 10;

    /// <summary>
    /// Fetches progression for no-clear members in ONE nested request: each member's recent reports are read
    /// once, with one aliased <c>fights</c> list per encounter they haven't cleared (both phases of a two-phase
    /// fight in the same read). The best pull per encounter is the wipe with the lowest <c>fightPercentage</c>
    /// (phase-aware, lower = closer to a kill); its <c>bossPercentage</c> and <c>lastPhase</c> drive a
    /// "P4 · 3%" display. Returns (member, encounter) → result for every requested pair it could answer, and
    /// the members whose progression couldn't be read (request failure or transient error; retryable).
    /// </summary>
    private async Task<(Dictionary<(int Member, int Encounter), EncounterParseResult> Results, HashSet<int> Failed)>
        FetchProgressionDataAsync(
            IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
            IReadOnlyDictionary<int, List<int>> encountersByMember)
    {
        var results = new Dictionary<(int Member, int Encounter), EncounterParseResult>();
        var failed = new HashSet<int>();
        var queryParts = new List<string>();
        var queried = new List<int>();

        foreach (var (i, encounters) in encountersByMember)
        {
            var (name, world, _) = members[i];
            var serverInfo = GetFFLogsServer(world);
            if (serverInfo is null)
            {
                continue;
            }

            // Savage raids share ONE encounter ID across Normal + Savage difficulties, so filter to Savage (101)
            // to keep a first-timer's Normal (Story-mode) pulls of that boss out. Single-difficulty content
            // (Ultimate / Extreme / Unreal / Chaotic) has no such overlap, so it needs no filter.
            var fightParts = new List<string>(encounters.Count);
            foreach (var encId in encounters)
            {
                var difficultyParam = GetDifficultyForEncounter(encId) == DifficultyHigh
                    ? $", difficulty: {DifficultyHigh}"
                    : string.Empty;
                fightParts.Add($"f{encId}: fights(encounterID: {encId}{difficultyParam}) {{ kill fightPercentage bossPercentage lastPhase friendlyPlayers }}");
            }

            var (slug, region) = serverInfo.Value;
            queried.Add(i);

            // masterData.actors + fights.friendlyPlayers let us keep ONLY the fights this character actually
            // took part in. recentReports.fights otherwise returns EVERY fight of the encounter in each
            // report, including other players' pulls in large shared/community logs — which would surface a
            // stranger's wipes as this player's progression.
            queryParts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ recentReports(limit: {ProgressionReportLimit}) {{ data {{ masterData {{ actors(type: ""Player"") {{ id name }} }} {string.Join(" ", fightParts)} }} }} }}");
        }

        if (queryParts.Count == 0)
        {
            return (results, failed);
        }

        var query = $"{{ characterData {{ {string.Join(" ", queryParts)} }} }}";
        var json = await QueryAsync(query);
        if (json is null)
        {
            // Couldn't tell: retry rather than cache a wrong "No logs".
            PassportCheckerReborn.Log.Warning("[FFLogsService] Progression query failed; members will be retried.");
            failed.UnionWith(queried);
            return (results, failed);
        }

        var answered = new HashSet<int>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            failed.UnionWith(GetTransientlyErroredAliases(doc.RootElement));

            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                foreach (var i in queried)
                {
                    if (failed.Contains(i))
                    {
                        continue;
                    }

                    // Each member on its own: an unexpected shape for one (left unanswered, so retried) mustn't
                    // cost the others their results.
                    try
                    {
                        if (charDataEl.TryGetProperty($"p{i}", out var charEl) &&
                            charEl.ValueKind == JsonValueKind.Object &&
                            charEl.TryGetProperty("recentReports", out var reportsEl) &&
                            reportsEl.ValueKind == JsonValueKind.Object &&
                            reportsEl.TryGetProperty("data", out var reportArr) &&
                            reportArr.ValueKind == JsonValueKind.Array)
                        {
                            var memberName = members[i].Name;
                            foreach (var encId in encountersByMember[i])
                            {
                                if (BestProgressionPull(reportArr, memberName, $"f{encId}") is { } best)
                                {
                                    results[(i, encId)] = best;
                                }
                            }
                        }

                        answered.Add(i);
                    }
                    catch (Exception ex)
                    {
                        PassportCheckerReborn.Log.Warning(ex, $"[FFLogsService] Failed to parse progression for member {i}.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse progression response.");
        }

        // A member we never got to read (bad response, parse failure) is retryable; one we read but found no
        // wipes for simply has no progression on that encounter.
        foreach (var i in queried)
        {
            if (!answered.Contains(i))
            {
                failed.Add(i);
                continue;
            }

            foreach (var encId in encountersByMember[i])
            {
                results.TryAdd((i, encId), NoDataResult());
            }
        }

        return (results, failed);
    }

    /// <summary>
    /// The best no-kill pull on one encounter across a character's recent reports: the lowest-fightPercentage
    /// wipe this character took part in, read from each report's aliased <paramref name="fightsAlias"/> list.
    /// Null when there is none.
    /// </summary>
    private static EncounterParseResult? BestProgressionPull(JsonElement reports, string memberName, string fightsAlias)
    {
        double? bestFightPct = null;
        double? bestBossPct = null;
        int? bestPhase = null;

        foreach (var report in reports.EnumerateArray())
        {
            if (report.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Actor ids for this character in this report (matched by name). recentReports is scoped to this
            // character, so they are present; used to drop other players' fights.
            var myActorIds = CollectActorIds(report, memberName);

            if (!report.TryGetProperty(fightsAlias, out var fightsEl) || fightsEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var fight in fightsEl.EnumerateArray())
            {
                // Skip kills – we only want wipe data for progression.
                if (fight.TryGetProperty("kill", out var killEl) &&
                    killEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                    killEl.GetBoolean())
                {
                    continue;
                }

                // Only count pulls this character actually participated in (see the query comment).
                if (!FightIncludesActor(fight, myActorIds))
                {
                    continue;
                }

                if (!fight.TryGetProperty("fightPercentage", out var fpEl) || fpEl.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var fp = fpEl.GetDouble();
                if (bestFightPct.HasValue && fp >= bestFightPct.Value)
                {
                    continue;
                }

                bestFightPct = fp;
                bestBossPct = fight.TryGetProperty("bossPercentage", out var bpEl) && bpEl.ValueKind == JsonValueKind.Number
                    ? bpEl.GetDouble()
                    : null;
                bestPhase = fight.TryGetProperty("lastPhase", out var lpEl) && lpEl.ValueKind == JsonValueKind.Number
                    ? lpEl.GetInt32()
                    : null;
            }
        }

        if (!bestFightPct.HasValue)
        {
            return null;
        }

        // Display value: boss HP % in the last phase reached, falling back to the fight %.
        return new EncounterParseResult(true, true, 0, null, bestBossPct ?? bestFightPct.Value)
        {
            ProgLastPhase = bestPhase,
        };
    }

    /// <summary>
    /// Actor ids in <paramref name="report"/> whose player name equals <paramref name="name"/>. Used to keep
    /// only the character's own fights, since a report can contain many other players' pulls (large shared or
    /// community logs). <c>recentReports</c> is scoped to this character, so a match is expected to exist.
    /// </summary>
    private static HashSet<int> CollectActorIds(JsonElement report, string name)
    {
        var ids = new HashSet<int>();
        if (report.TryGetProperty("masterData", out var md)
            && md.ValueKind == JsonValueKind.Object
            && md.TryGetProperty("actors", out var actors)
            && actors.ValueKind == JsonValueKind.Array)
        {
            foreach (var actor in actors.EnumerateArray())
            {
                if (actor.TryGetProperty("name", out var an) && an.ValueKind == JsonValueKind.String
                    && string.Equals(an.GetString(), name, StringComparison.Ordinal)
                    && actor.TryGetProperty("id", out var ai) && ai.ValueKind == JsonValueKind.Number)
                {
                    ids.Add(ai.GetInt32());
                }
            }
        }

        return ids;
    }

    /// <summary>
    /// True when the fight's <c>friendlyPlayers</c> include one of <paramref name="actorIds"/>. Returns false
    /// for an empty set (character not identifiable in this report) so an unattributable fight is never counted
    /// as the character's progression.
    /// </summary>
    private static bool FightIncludesActor(JsonElement fight, HashSet<int> actorIds)
    {
        if (actorIds.Count == 0)
        {
            return false;
        }

        if (!fight.TryGetProperty("friendlyPlayers", out var fp) || fp.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var p in fp.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.Number && actorIds.Contains(p.GetInt32()))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Builds the FFLogs character-page URL directly from a name + world — no API call, so it costs zero
    /// rate-limit points. Returns <c>null</c> when the world can't be mapped to a server.
    /// Works for KR too (e.g. <c>.../character/kr/moogle/&lt;name&gt;</c>).
    /// </summary>
    public static string? GetCharacterPageUrl(string playerName, string worldName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return null;
        }

        if (GetFFLogsServer(worldName) is not { } serverInfo)
        {
            return null;
        }

        var (slug, region) = serverInfo;
        return $"https://www.fflogs.com/character/{region.ToLowerInvariant()}/{slug}/{Uri.EscapeDataString(playerName)}";
    }

    // ─────────────────────────────────────────────────────────────────────────
    // World → FFLogs slug/region mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Maps an FFXIV home-world name to the FFLogs <c>serverSlug</c> and
    /// <c>serverRegion</c> values.
    /// </summary>
    /// <returns>
    /// A tuple of (serverSlug, serverRegion) or <c>null</c> if the world is unknown.
    /// The serverSlug is the world name lowercased; serverRegion is the data-center region.
    /// </returns>
    public static (string ServerSlug, string ServerRegion)? GetFFLogsServer(string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return null;
        }

        // Korean worlds report their name in Korean (e.g. "모그리"); FFLogs expects the
        // English server slug ("moogle") with region "KR". This must be checked BEFORE the
        // English NA/EU/JP sets — several KR world names collide with existing English worlds
        // (Moogle/Carbuncle/Chocobo/Tonberry/Fenrir), but keying on the Korean name is
        // collision-free because the other sets are all English.
        if (KrWorldSlugs.TryGetValue(worldName, out var krSlug))
        {
            return (krSlug, "KR");
        }

        // FFLogs uses the world name as the slug (lowercased)
        var slug = worldName.ToLowerInvariant();

        // Map known worlds to their data-center region
        if (NaWorlds.Contains(worldName))
        {
            return (slug, "NA");
        }

        if (EuWorlds.Contains(worldName))
        {
            return (slug, "EU");
        }

        if (JpWorlds.Contains(worldName))
        {
            return (slug, "JP");
        }

        if (OcWorlds.Contains(worldName))
        {
            return (slug, "OC");
        }

        // Fallback: attempt NA (most common for English clients)
        return (slug, "NA");
    }

    // NA worlds (Aether, Primal, Crystal, Dynamis)
    private static readonly HashSet<string> NaWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Aether
        "Adamantoise", "Cactuar", "Faerie", "Gilgamesh", "Jenova", "Midgardsormr", "Sargatanas", "Siren",
        // Primal
        "Behemoth", "Excalibur", "Exodus", "Famfrit", "Hyperion", "Lamia", "Leviathan", "Ultros",
        // Crystal
        "Balmung", "Brynhildr", "Coeurl", "Diabolos", "Goblin", "Malboro", "Mateus", "Zalera",
        // Dynamis
        "Halicarnassus", "Maduin", "Marilith", "Seraph", "Cuchulainn", "Golem", "Kraken", "Rafflesia",
    };

    // EU worlds (Chaos, Light, Shadow)
    private static readonly HashSet<string> EuWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Chaos
        "Cerberus", "Louisoix", "Moogle", "Omega", "Phantom", "Ragnarok", "Sagittarius", "Spriggan",
        // Light
        "Alpha", "Lich", "Odin", "Phoenix", "Raiden", "Shiva", "Twintania", "Zodiark",
        // Shadow
        "Innocence", "Pixie", "Titania",
    };

    // JP worlds (Elemental, Gaia, Mana, Meteor)
    private static readonly HashSet<string> JpWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Elemental
        "Aegis", "Atomos", "Carbuncle", "Garuda", "Gungnir", "Kujata", "Tonberry", "Typhon",
        // Gaia
        "Alexander", "Bahamut", "Durandal", "Fenrir", "Ifrit", "Ridill", "Tiamat", "Ultima",
        // Mana
        "Anima", "Asura", "Chocobo", "Hades", "Ixion", "Masamune", "Pandaemonium", "Titan",
        // Meteor
        "Belias", "Mandragora", "Ramuh", "Shinryu", "Unicorn", "Valefor", "Yojimbo", "Zeromus",
    };

    // OC worlds (Materia)
    private static readonly HashSet<string> OcWorlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Bismarck", "Ravana", "Sephirot", "Sophia", "Zurvan",
    };

    // KR worlds (한국 data center) — keyed by the Korean world name the KR client reports.
    // Values are the English FFLogs server slugs; region is always "KR".
    // e.g. https://ko.fflogs.com/character/kr/moogle/<name>
    private static readonly Dictionary<string, string> KrWorldSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["모그리"] = "moogle",
        ["초코보"] = "chocobo",
        ["카벙클"] = "carbuncle",
        ["톤베리"] = "tonberry",
        ["펜리르"] = "fenrir",
    };

    // ─────────────────────────────────────────────────────────────────────────
    // Job abbreviation ↔ FFLogs spec mapping
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Maps FFXIV job abbreviations to FFLogs spec names.</summary>
    private static readonly Dictionary<string, string> JobAbbrevToSpec = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PLD"] = "Paladin", ["WAR"] = "Warrior", ["DRK"] = "DarkKnight", ["GNB"] = "Gunbreaker",
        ["WHM"] = "WhiteMage", ["SCH"] = "Scholar", ["AST"] = "Astrologian", ["SGE"] = "Sage",
        ["MNK"] = "Monk", ["DRG"] = "Dragoon", ["NIN"] = "Ninja", ["SAM"] = "Samurai",
        ["RPR"] = "Reaper", ["VPR"] = "Viper",
        ["BRD"] = "Bard", ["MCH"] = "Machinist", ["DNC"] = "Dancer",
        ["BLM"] = "BlackMage", ["SMN"] = "Summoner", ["RDM"] = "RedMage", ["PCT"] = "Pictomancer",
    };

    /// <summary>Maps FFLogs spec names to FFXIV job abbreviations.</summary>
    private static readonly Dictionary<string, string> SpecToJobAbbrev = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Paladin"] = "PLD", ["Warrior"] = "WAR", ["DarkKnight"] = "DRK", ["Gunbreaker"] = "GNB",
        ["WhiteMage"] = "WHM", ["Scholar"] = "SCH", ["Astrologian"] = "AST", ["Sage"] = "SGE",
        ["Monk"] = "MNK", ["Dragoon"] = "DRG", ["Ninja"] = "NIN", ["Samurai"] = "SAM",
        ["Reaper"] = "RPR", ["Viper"] = "VPR",
        ["Bard"] = "BRD", ["Machinist"] = "MCH", ["Dancer"] = "DNC",
        ["BlackMage"] = "BLM", ["Summoner"] = "SMN", ["RedMage"] = "RDM", ["Pictomancer"] = "PCT",
    };

    /// <summary>Maps FFLogs spec names to FFXIV game icon IDs (62100 + ClassJob RowId).</summary>
    private static readonly Dictionary<string, uint> SpecToJobIconId = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Paladin"] = 62119, ["Warrior"] = 62121, ["DarkKnight"] = 62132, ["Gunbreaker"] = 62137,
        ["WhiteMage"] = 62124, ["Scholar"] = 62128, ["Astrologian"] = 62133, ["Sage"] = 62140,
        ["Monk"] = 62120, ["Dragoon"] = 62122, ["Ninja"] = 62130, ["Samurai"] = 62134,
        ["Reaper"] = 62139, ["Viper"] = 62141,
        ["Bard"] = 62123, ["Machinist"] = 62131, ["Dancer"] = 62138,
        ["BlackMage"] = 62125, ["Summoner"] = 62127, ["RedMage"] = 62135, ["Pictomancer"] = 62142,
    };

    /// <summary>
    /// Resolves a player's current FFXIV job abbreviation to the FFLogs spec name.
    /// Returns <c>null</c> if the abbreviation is unknown.
    /// </summary>
    public static string? GetSpecForJob(string? jobAbbreviation)
    {
        if (string.IsNullOrWhiteSpace(jobAbbreviation))
        {
            return null;
        }

        return JobAbbrevToSpec.GetValueOrDefault(jobAbbreviation!);
    }

    /// <summary>
    /// Resolves an FFLogs spec name to the FFXIV job abbreviation.
    /// Returns <c>null</c> if the spec is unknown.
    /// </summary>
    public static string? GetJobAbbrevForSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        return SpecToJobAbbrev.GetValueOrDefault(spec!);
    }

    /// <summary>
    /// Returns the game icon ID for a given FFLogs spec name, or <c>null</c> if unknown.
    /// </summary>
    public static uint? GetJobIconIdForSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
        {
            return null;
        }

        return SpecToJobIconId.GetValueOrDefault(spec!);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string EscapeGraphQL(string input) =>
        input.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// From a GraphQL response root, returns the set of member alias indices (the <c>p{i}</c> in
    /// <c>characterData.p{i}</c>) whose <c>errors[]</c> entry looks like a TRANSIENT server-side failure
    /// (rate limit, timeout, 5xx, "try again") rather than the character simply not existing. A null alias
    /// caused by a transient error must be treated as a retryable fetch-failure — NOT cached as a permanent
    /// "No logs" — otherwise a real player flickers to "No logs" for the whole cache window. A null alias
    /// with no error (or a plain not-found error) stays a genuine "no data" and is cached as before,
    /// preserving the point-saving behaviour for the common not-found case.
    /// </summary>
    /// <summary>Member indices (<c>p{i}</c> aliases) named in the path of ANY GraphQL error, transient or not.</summary>
    private static HashSet<int> GetErroredAliases(JsonElement root)
    {
        var set = new HashSet<int>();
        if (!root.TryGetProperty("errors", out var errorsEl) || errorsEl.ValueKind != JsonValueKind.Array)
        {
            return set;
        }

        foreach (var err in errorsEl.EnumerateArray())
        {
            if (err.ValueKind != JsonValueKind.Object
                || !err.TryGetProperty("path", out var pathEl)
                || pathEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var seg in pathEl.EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.String
                    && seg.GetString() is { Length: > 1 } s && s[0] == 'p'
                    && int.TryParse(s.AsSpan(1), out var idx))
                {
                    set.Add(idx);
                }
            }
        }

        return set;
    }

    private static HashSet<int> GetTransientlyErroredAliases(JsonElement root)
    {
        var set = new HashSet<int>();
        if (!root.TryGetProperty("errors", out var errorsEl) || errorsEl.ValueKind != JsonValueKind.Array)
        {
            return set;
        }

        foreach (var err in errorsEl.EnumerateArray())
        {
            var message = err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;
            if (!LooksTransient(message))
            {
                continue;
            }

            if (!err.TryGetProperty("path", out var pathEl) || pathEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var seg in pathEl.EnumerateArray())
            {
                if (seg.ValueKind == JsonValueKind.String
                    && seg.GetString() is { Length: > 1 } s && s[0] == 'p'
                    && int.TryParse(s.AsSpan(1), out var idx))
                {
                    set.Add(idx);
                }
            }
        }

        return set;
    }

    /// <summary>Heuristic: does a GraphQL error message describe a transient/retryable failure (vs a
    /// character-not-found)? Blank/unknown messages are treated as non-transient so we default to caching a
    /// no-data result rather than re-spending points chasing a name that simply isn't on FFLogs.</summary>
    private static bool LooksTransient(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        ReadOnlySpan<string> markers =
        [
            "rate limit", "ratelimit", "too many", "timeout", "timed out", "try again",
            "temporar", "internal", "unexpected", "server error", "500", "502", "503", "504", "429",
        ];
        foreach (var mk in markers)
        {
            if (message.Contains(mk, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        // Stop any in-flight request first, then tear down the client and the token source.
        lifetimeCts.Cancel();
        httpClient.Dispose();
        lifetimeCts.Dispose();
    }
}

/// <summary>
/// Data representing a player's parse data for a specific encounter (or a general parse fallback).
/// </summary>
public record EncounterParseResult(
    bool HasData,
    bool IsEncounterSpecific,
    int TotalKills,
    double? BestParse,
    double? LowestBossHpPct)
{
    public double? Phase1BestParse { get; init; }
    public double? Phase2BestParse { get; init; }
    public double? Phase2LowestBossHpPct { get; init; }
    public int? Phase1TotalKills { get; init; }
    public int? Phase2TotalKills { get; init; }

    /// <summary>For a two-phase fight, the FULL single-encounter result of each phase (P1/P2), so the overlay
    /// can render each phase with its own kills and own parse/progression. Null on single-phase fights.</summary>
    public EncounterParseResult? Phase1Result { get; init; }
    public EncounterParseResult? Phase2Result { get; init; }

    /// <summary>
    /// True when the lookup request itself failed (network error, rate limit, bad response) rather than the
    /// player genuinely having no logs. Lets the UI show "lookup failed" instead of a misleading "No logs".
    /// Failure results are never cached, so the next refresh retries.
    /// </summary>
    public bool FetchFailed { get; init; }

    /// <summary>
    /// True when FFLogs has no character with this name on this server (never uploaded, or renamed /
    /// transferred since). Unlike <see cref="FetchFailed"/> this is a real answer, so it is cached.
    /// </summary>
    public bool CharacterNotFound { get; init; }

    /// <summary>
    /// True when the player has no clear and progression wasn't looked up because FFLogs doesn't record it for
    /// this duty (only the current Savage tier and Ultimate). "No logs" then means "no clear", not "never tried".
    /// </summary>
    public bool ProgressionNotTracked { get; init; }

    /// <summary>
    /// True when the character has made their FFLogs rankings private, so whether they cleared is unknown.
    /// A real answer, so it is cached.
    /// </summary>
    public bool RankingsHidden { get; init; }

    /// <summary>
    /// For a no-kill player, the phase (lastPhase) reached on their best progression pull, so the UI can
    /// show e.g. "P4 · 3%". Null for single-phase fights or when phase data is unavailable.
    /// </summary>
    public int? ProgLastPhase { get; init; }

    /// <summary>Best parse on the player's current job for this encounter.</summary>
    public double? CurrentJobBestParse { get; init; }

    /// <summary>
    /// Best parse per FFLogs spec. Cached results are keyed by player rather than job, so this lets the
    /// current-job parse be re-derived when the same player is seen on another job, without a new request.
    /// </summary>
    public IReadOnlyDictionary<string, double>? BestParseBySpec { get; init; }

    /// <summary>FFLogs spec name for the overall best parse (any job).</summary>
    public string? BestParseSpec { get; init; }

    /// <summary>FFXIV job abbreviation for the overall best parse job.</summary>
    public string? BestParseJobAbbreviation { get; init; }

    /// <summary>Icon ID for the overall best parse job.</summary>
    public uint? BestParseJobIconId { get; init; }
}
