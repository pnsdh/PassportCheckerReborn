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

    private string? cachedToken;
    private DateTime tokenExpiry = DateTime.MinValue;

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
    /// Returns <c>true</c> when a token is successfully issued.
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

    private async Task<string?> FetchTokenAsync(string clientId, string clientSecret)
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
            if (!doc.RootElement.TryGetProperty("access_token", out var tokenEl))
            {
                return null;
            }

            var token = tokenEl.GetString();

            // Cache the token
            cachedToken = token;
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var expEl)
                ? expEl.GetInt32() : 3600;
            tokenExpiry = DateTime.UtcNow.AddSeconds(expiresIn - 60); // 60-second safety margin

            PassportCheckerReborn.Log.Information("[FFLogsService] Token obtained successfully.");
            return token;
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Exception during token fetch.");
            return null;
        }
    }

    /// <summary>Returns a valid Bearer token, refreshing if necessary.</summary>
    private async Task<string?> GetTokenAsync()
    {
        var cfg = plugin.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.FFLogsClientId) ||
            string.IsNullOrWhiteSpace(cfg.FFLogsClientSecret))
        {
            return null;
        }

        if (cachedToken is not null && DateTime.UtcNow < tokenExpiry)
        {
            return cachedToken;
        }

        return await FetchTokenAsync(cfg.FFLogsClientId, cfg.FFLogsClientSecret);
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
                cachedToken = null;
                tokenExpiry = DateTime.MinValue;
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
    private readonly record struct DutyEntry(
        uint DutyId, string Name, int ZoneId, int PrimaryEncounterId, int? SecondaryEncounterId, int Difficulty,
        int[]? HistoricalEncounterIds = null);

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

        // ── Savage — AAC Heavyweight (M4 is two phases) ──
        new(1069, "AAC Heavyweight M1 (Savage)",       73, 101, null, DifficultyHigh),
        new(1071, "AAC Heavyweight M2 (Savage)",       73, 102, null, DifficultyHigh),
        new(1073, "AAC Heavyweight M3 (Savage)",       73, 103, null, DifficultyHigh),
        new(1075, "AAC Heavyweight M4 (Savage)",       73, 104, 105,  DifficultyHigh),

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
        new(1094, "Dancing Mad (Ultimate)",                  76, 1085, null, DifficultyNormal),
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
    /// Fetches encounter-specific data for all members in a batch.
    /// <para>Phase 1: Query encounterRankings for all characters.</para>
    /// <para>Phase 2: For characters with no kills, query their recent reports.</para>
    /// <para>Phase 3: Get fight percentages from those reports to find best progression.</para>
    /// </summary>
    public async Task<Dictionary<int, EncounterParseResult>> GetEncounterDataForAllAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members, int encounterId,
        Action<int, EncounterParseResult>? onUpdated = null)
    {
        var results = new Dictionary<int, EncounterParseResult>();

        // Phase 1: Build a single GraphQL query for encounterRankings for all members
        var queryParts = new List<string>();
        var validIndices = new List<int>();
        var difficultyParam = GetDifficultyForEncounter(encounterId) is { } diff
            ? $", difficulty: {diff}"
            : string.Empty;

        for (var i = 0; i < members.Count; i++)
        {
            var (name, world, _) = members[i];
            var serverInfo = GetFFLogsServer(world);
            if (serverInfo is null)
            {
                results[i] = new EncounterParseResult(false, true, 0, null, null);
                continue;
            }

            var (slug, region) = serverInfo.Value;
            validIndices.Add(i);
            queryParts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ encounterRankings(encounterID: {encounterId}{difficultyParam}) }}");
        }

        if (queryParts.Count == 0)
        {
            return results;
        }

        var fullQuery = $"{{ characterData {{ {string.Join(" ", queryParts)} }} }}";
        var json = await QueryAsync(fullQuery);

        if (json is null)
        {
            foreach (var i in validIndices)
            {
                var failed = new EncounterParseResult(false, true, 0, null, null) { FetchFailed = true };
                results[i] = failed;
                onUpdated?.Invoke(i, failed);
            }

            return results;
        }

        var noKillIndices = new List<int>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                PassportCheckerReborn.Log.Warning(
                    "[FFLogsService] Encounter batch response missing data/characterData.");
                foreach (var i in validIndices)
                {
                    var failed = new EncounterParseResult(false, true, 0, null, null) { FetchFailed = true };
                    results[i] = failed;
                    onUpdated?.Invoke(i, failed);
                }

                return results;
            }

            var transientErrored = GetTransientlyErroredAliases(doc.RootElement);

            foreach (var i in validIndices)
            {
                if (!charDataEl.TryGetProperty($"p{i}", out var charEl) ||
                    charEl.ValueKind == JsonValueKind.Null)
                {
                    // A transient per-alias error → retryable (uncached) fetch-failure, not a real "No logs".
                    results[i] = transientErrored.Contains(i)
                        ? new EncounterParseResult(false, true, 0, null, null) { FetchFailed = true }
                        : new EncounterParseResult(false, true, 0, null, null);
                    continue;
                }

                if (!charEl.TryGetProperty("encounterRankings", out var rankingsEl) ||
                    rankingsEl.ValueKind == JsonValueKind.Null)
                {
                    results[i] = new EncounterParseResult(false, true, 0, null, null);
                    continue;
                }

                var totalKills = 0;
                if (rankingsEl.TryGetProperty("totalKills", out var tkEl) &&
                    tkEl.ValueKind == JsonValueKind.Number)
                {
                    totalKills = tkEl.GetInt32();
                }

                if (totalKills > 0)
                {
                    double? bestParse = null;
                    string? bestParseSpec = null;
                    double? currentJobBestParse = null;

                    var currentJobSpec = GetSpecForJob(members[i].JobAbbreviation);

                    if (rankingsEl.TryGetProperty("ranks", out var ranksEl) &&
                        ranksEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var rank in ranksEl.EnumerateArray())
                        {
                            if (rank.TryGetProperty("rankPercent", out var pctEl) &&
                                pctEl.ValueKind == JsonValueKind.Number)
                            {
                                var pct = pctEl.GetDouble();
                                var spec = rank.TryGetProperty("spec", out var specEl)
                                    ? specEl.GetString() : null;

                                // Track overall best parse (any job)
                                if (!bestParse.HasValue || pct > bestParse.Value)
                                {
                                    bestParse = pct;
                                    bestParseSpec = spec;
                                }

                                // Track best parse for the player's current job
                                if (currentJobSpec != null && spec != null &&
                                    string.Equals(spec, currentJobSpec, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (!currentJobBestParse.HasValue || pct > currentJobBestParse.Value)
                                    {
                                        currentJobBestParse = pct;
                                    }
                                }
                            }
                        }
                    }

                    results[i] = new EncounterParseResult(true, true, totalKills, bestParse, null)
                    {
                        CurrentJobBestParse = currentJobBestParse,
                        BestParseSpec = bestParseSpec,
                        BestParseJobAbbreviation = GetJobAbbrevForSpec(bestParseSpec),
                        BestParseJobIconId = GetJobIconIdForSpec(bestParseSpec),
                    };
                }
                else
                {
                    noKillIndices.Add(i);
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[FFLogsService] Failed to parse encounter rankings batch response.");
            foreach (var i in validIndices)
            {
                if (!results.ContainsKey(i))
                {
                    results[i] = new EncounterParseResult(false, true, 0, null, null) { FetchFailed = true };
                }
            }
            return results;
        }

        // Publish the fast kill/parse results now, so the UI can render them before the slower
        // progression query (below) resolves for the no-kill members.
        if (onUpdated is not null)
        {
            foreach (var (i, r) in results)
            {
                if (r.HasData)
                {
                    onUpdated(i, r);
                }
            }
        }

        // Phases 2 & 3: Get progression data for players with no kills
        if (noKillIndices.Count > 0)
        {
            await FetchProgressionDataAsync(members, encounterId, noKillIndices, results, onUpdated);
        }

        return results;
    }

    /// <summary>
    /// Fetches encounter-specific data for multi-part encounters (e.g., P1/P2) for all members.
    /// </summary>
    public async Task<Dictionary<int, EncounterParseResult>> GetMultiEncounterDataForAllAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members, int phase1EncounterId, int phase2EncounterId)
    {
        var phase1Results = await GetEncounterDataForAllAsync(members, phase1EncounterId);
        var phase2Results = await GetEncounterDataForAllAsync(members, phase2EncounterId);

        var combined = new Dictionary<int, EncounterParseResult>();
        for (var i = 0; i < members.Count; i++)
        {
            phase1Results.TryGetValue(i, out var phase1);
            phase2Results.TryGetValue(i, out var phase2);

            var phase1Kills = phase1?.TotalKills ?? 0;
            var phase2Kills = phase2?.TotalKills ?? 0;
            var fullClears = Math.Min(phase1Kills, phase2Kills);
            var hasData = (phase1?.HasData ?? false) || (phase2?.HasData ?? false);

            // Determine best job info from phase results (prefer P2, fall back to P1)
            var bestPhase = (phase2?.HasData == true && phase2.TotalKills > 0) ? phase2
                          : (phase1?.HasData == true && phase1.TotalKills > 0) ? phase1
                          : phase2 ?? phase1;

            // Determine CurrentJobBestParse: take the value from whichever phase has it
            double? currentJobBestParse;
            if (phase2?.CurrentJobBestParse.HasValue == true && phase1?.CurrentJobBestParse.HasValue == true)
            {
                currentJobBestParse = Math.Max(phase1.CurrentJobBestParse!.Value, phase2.CurrentJobBestParse!.Value);
            }
            else
            {
                currentJobBestParse = phase2?.CurrentJobBestParse ?? phase1?.CurrentJobBestParse;
            }

            // BestParse for the combined result – the overall best parse (any job) from
            // the most relevant phase so DrawBestParseOnDifferentJob can display it.
            var combinedBestParse = bestPhase?.BestParse;

            combined[i] = new EncounterParseResult(hasData, true, fullClears, combinedBestParse, null)
            {
                // Carry a fetch-failure from either phase so the caller retries instead of caching no-data.
                FetchFailed = (phase1?.FetchFailed ?? false) || (phase2?.FetchFailed ?? false),
                Phase1BestParse = phase1?.BestParse,
                Phase2BestParse = phase2?.BestParse,
                Phase2LowestBossHpPct = phase2?.LowestBossHpPct,
                Phase1TotalKills = phase1Kills,
                Phase2TotalKills = phase2Kills,
                CurrentJobBestParse = currentJobBestParse,
                BestParseSpec = bestPhase?.BestParseSpec,
                BestParseJobAbbreviation = bestPhase?.BestParseJobAbbreviation,
                BestParseJobIconId = bestPhase?.BestParseJobIconId,
            };
        }

        return combined;
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

    /// <summary>
    /// Does the real API work for a cache-miss subset of members (indexed 0..n-1): the current-expansion
    /// listing, any older-expansion (historical) listings folded in, no-kill progression, and a zone-average
    /// fallback. For Ultimates the current + all historical listings are fetched in a SINGLE batched request.
    /// </summary>
    private async Task<Dictionary<int, EncounterParseResult>> ComputeDutyDataAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        DutyEntry entry,
        Action<int, EncounterParseResult>? onUpdated = null)
    {
        Dictionary<int, EncounterParseResult> results;

        if (entry.HistoricalEncounterIds is { Length: > 0 } historical)
        {
            // Ultimates: one request covers the current + every older-expansion listing (all single-phase).
            var encounterIds = new List<int>(historical.Length + 1) { entry.PrimaryEncounterId };
            encounterIds.AddRange(historical);

            var raw = await QueryEncounterRankingsBatchAsync(members, encounterIds);
            results = new Dictionary<int, EncounterParseResult>();

            if (raw is null)
            {
                // The batched request failed — mark resolvable members as fetch-failed (retryable, uncached).
                for (var i = 0; i < members.Count; i++)
                {
                    if (string.IsNullOrEmpty(members[i].Name))
                    {
                        continue;
                    }

                    var failed = new EncounterParseResult(false, true, 0, null, null) { FetchFailed = true };
                    results[i] = failed;
                    onUpdated?.Invoke(i, failed);
                }
            }
            else
            {
                for (var i = 0; i < members.Count; i++)
                {
                    if (!raw.TryGetValue(i, out var perEncounter))
                    {
                        continue;
                    }

                    var current = perEncounter.TryGetValue(entry.PrimaryEncounterId, out var primary)
                        ? BuildSinglePhaseResult(primary)
                        : new EncounterParseResult(false, true, 0, null, null);

                    foreach (var histId in historical)
                    {
                        if (perEncounter.TryGetValue(histId, out var hist))
                        {
                            current = MergeHistorical(current, hist);
                        }
                    }

                    results[i] = current;

                    // Publish clears/parses immediately; no-log members wait for the slower progression step.
                    if (current.HasData)
                    {
                        onUpdated?.Invoke(i, current);
                    }
                }

                // Progression only where there's no clear in ANY expansion, measured against the live listing.
                var noKill = new List<int>();
                for (var i = 0; i < members.Count; i++)
                {
                    if (string.IsNullOrEmpty(members[i].Name))
                    {
                        continue;
                    }

                    if (!results.TryGetValue(i, out var r) || r.TotalKills == 0)
                    {
                        noKill.Add(i);
                    }
                }

                if (noKill.Count > 0)
                {
                    await FetchProgressionDataAsync(members, entry.PrimaryEncounterId, noKill, results, onUpdated);
                }
            }
        }
        else if (entry.SecondaryEncounterId is { } secondary)
        {
            results = await GetMultiEncounterDataForAllAsync(members, entry.PrimaryEncounterId, secondary);

            // Multi-phase isn't split into fast/slow steps here; publish the combined results in one go.
            if (onUpdated is not null)
            {
                foreach (var (i, r) in results)
                {
                    if (r.HasData)
                    {
                        onUpdated(i, r);
                    }
                }
            }
        }
        else
        {
            results = await GetEncounterDataForAllAsync(members, entry.PrimaryEncounterId, onUpdated);
        }

        // No zone-average fallback for mapped duties on purpose: for a specific encounter we only want that
        // encounter's data — a current-tier average was more confusing than helpful, and skipping it also
        // avoids spending points on it. (The unmapped-duty path still shows a zone average as its only signal.)
        return results;
    }

    /// <summary>
    /// One-request batch of encounterRankings for every (member × encounter) pair via GraphQL aliases, so a
    /// single HTTP round-trip covers all members and all encounter IDs. Returns memberIndex → (encounterId →
    /// parsed rank data); missing entries mean no data. Returns <c>null</c> when the request itself failed
    /// (so callers can distinguish a failure from "everyone has no data"). Progression is NOT fetched here.
    /// </summary>
    private async Task<Dictionary<int, Dictionary<int, EncounterRankData>>?> QueryEncounterRankingsBatchAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        IReadOnlyList<int> encounterIds)
    {
        var result = new Dictionary<int, Dictionary<int, EncounterRankData>>();
        if (encounterIds.Count == 0)
        {
            return result;
        }

        var memberParts = new List<string>();
        var validIndices = new List<int>();
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

            validIndices.Add(i);
            memberParts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ {string.Join(" ", encounterParts)} }}");
        }

        if (memberParts.Count == 0)
        {
            return result;
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

            foreach (var i in validIndices)
            {
                if (!charDataEl.TryGetProperty($"p{i}", out var charEl) || charEl.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                var currentJobSpec = GetSpecForJob(members[i].JobAbbreviation);
                var perEncounter = new Dictionary<int, EncounterRankData>();
                foreach (var encId in encounterIds)
                {
                    if (charEl.TryGetProperty($"e{encId}", out var rankingsEl) && rankingsEl.ValueKind != JsonValueKind.Null)
                    {
                        perEncounter[encId] = ParseEncounterRank(rankingsEl, currentJobSpec);
                    }
                }

                if (perEncounter.Count > 0)
                {
                    result[i] = perEncounter;
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex, "[FFLogsService] Failed to parse batched encounter rankings.");
        }

        return result;
    }

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

        return new EncounterRankData(totalKills, bestParse, bestParseSpec, currentJobBestParse);
    }

    /// <summary>Builds a single-phase <see cref="EncounterParseResult"/> from one encounter's raw rank data.</summary>
    private static EncounterParseResult BuildSinglePhaseResult(EncounterRankData d)
        => new(d.TotalKills > 0, true, d.TotalKills, d.BestParse, null)
        {
            CurrentJobBestParse = d.CurrentJobBestParse,
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

        var averages = await QueryZoneAveragesAsync(members, indices);
        var results = new Dictionary<int, EncounterParseResult>();
        foreach (var (i, avg) in averages)
        {
            results[i] = avg.HasValue
                ? new EncounterParseResult(true, false, 0, avg.Value, null)
                : new EncounterParseResult(false, false, 0, null, null);
        }

        return results;
    }

    /// <summary>
    /// Batched <c>zoneRankings</c> lookup for the given member indices in a single request, returning
    /// memberIndex → best (or median) performance average. Results are cached per character for
    /// <see cref="CacheTtl"/>, so cached members are served without hitting the API.
    /// </summary>
    private async Task<Dictionary<int, double?>> QueryZoneAveragesAsync(
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
            return result;
        }

        var query = $"{{ characterData {{ {string.Join(" ", parts)} }} }}";
        var json = await QueryAsync(query);
        if (json is null)
        {
            return result;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) ||
                !dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                return result;
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
        return result;
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
        int TotalKills, double? BestParse, string? BestParseSpec, double? CurrentJobBestParse);

    // How many recent reports per player to scan for progression pulls. Larger = catches older prog at
    // the cost of more API points. FFLogs has no "furthest pull ever" field, so this is the practical window.
    private const int ProgressionReportLimit = 10;

    /// <summary>
    /// Fetches progression data for no-kill players in ONE nested request: for each player, pulls the wipes
    /// on this encounter straight from their recent reports (<c>recentReports.data.fights</c>) — no separate
    /// report-code round-trip. The best pull is the wipe with the lowest <c>fightPercentage</c> (phase-aware,
    /// lower = closer to a kill); its <c>bossPercentage</c> and <c>lastPhase</c> drive a "P4 · 3%" display.
    /// </summary>
    private async Task FetchProgressionDataAsync(
        IReadOnlyList<(string Name, string World, string JobAbbreviation)> members,
        int encounterId,
        List<int> noKillIndices,
        Dictionary<int, EncounterParseResult> results,
        Action<int, EncounterParseResult>? onUpdated = null)
    {
        // Emits each no-kill member's final result (prog or no-logs) once the progression query resolves.
        void PublishNoKill()
        {
            if (onUpdated is null)
            {
                return;
            }

            foreach (var i in noKillIndices)
            {
                if (results.TryGetValue(i, out var r))
                {
                    onUpdated(i, r);
                }
            }
        }

        var queryParts = new List<string>();
        var validIndices = new List<int>();

        // Savage raids share ONE encounter ID across Normal + Savage difficulties, so filter to Savage (101)
        // to keep a first-timer's Normal (Story-mode) pulls of that boss out. Single-difficulty content
        // (Ultimate / Extreme / Unreal / Chaotic) has no such overlap, so it needs no filter.
        var difficultyParam = GetDifficultyForEncounter(encounterId) == DifficultyHigh
            ? $", difficulty: {DifficultyHigh}"
            : string.Empty;

        foreach (var i in noKillIndices)
        {
            var (name, world, _) = members[i];
            var serverInfo = GetFFLogsServer(world);
            if (serverInfo is null)
            {
                continue;
            }

            var (slug, region) = serverInfo.Value;
            validIndices.Add(i);
            // masterData.actors + fights.friendlyPlayers let us keep ONLY the fights this character actually
            // took part in. recentReports.fights otherwise returns EVERY fight of the encounter in each
            // report, including other players' pulls in large shared/community logs — which would surface a
            // stranger's wipes as this player's progression.
            queryParts.Add(
                $@"p{i}: character(name: ""{EscapeGraphQL(name)}"", serverSlug: ""{EscapeGraphQL(slug)}"", serverRegion: ""{EscapeGraphQL(region)}"") {{ recentReports(limit: {ProgressionReportLimit}) {{ data {{ masterData {{ actors(type: ""Player"") {{ id name }} }} fights(encounterID: {encounterId}{difficultyParam}) {{ kill fightPercentage bossPercentage lastPhase friendlyPlayers }} }} }} }}");
        }

        if (queryParts.Count == 0)
        {
            foreach (var i in noKillIndices)
            {
                results.TryAdd(i, new EncounterParseResult(false, true, 0, null, null));
            }

            PublishNoKill();
            return;
        }

        var query = $"{{ characterData {{ {string.Join(" ", queryParts)} }} }}";
        var json = await QueryAsync(query);

        if (json is null)
        {
            PassportCheckerReborn.Log.Warning(
                "[FFLogsService] Progression query failed; marking no-kill players as no logs.");
            foreach (var i in noKillIndices)
            {
                results.TryAdd(i, new EncounterParseResult(false, true, 0, null, null));
            }

            PublishNoKill();
            return;
        }

        var transientErrored = new HashSet<int>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            transientErrored = GetTransientlyErroredAliases(doc.RootElement);
            if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                dataEl.TryGetProperty("characterData", out var charDataEl))
            {
                foreach (var i in validIndices)
                {
                    if (!charDataEl.TryGetProperty($"p{i}", out var charEl) ||
                        charEl.ValueKind == JsonValueKind.Null ||
                        !charEl.TryGetProperty("recentReports", out var reportsEl) ||
                        !reportsEl.TryGetProperty("data", out var reportArr) ||
                        reportArr.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    // Best pull across recent reports = the lowest-fightPercentage wipe THIS player was in.
                    double? bestFightPct = null;
                    double? bestBossPct = null;
                    int? bestPhase = null;
                    var memberName = members[i].Name;

                    foreach (var report in reportArr.EnumerateArray())
                    {
                        // Actor ids for this character in this report (matched by name). recentReports is
                        // scoped to this character, so they are present; used to drop other players' fights.
                        var myActorIds = CollectActorIds(report, memberName);

                        if (!report.TryGetProperty("fights", out var fightsEl) ||
                            fightsEl.ValueKind != JsonValueKind.Array)
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

                            // Only count pulls this character actually participated in (see query comment).
                            if (!FightIncludesActor(fight, myActorIds))
                            {
                                continue;
                            }

                            if (!fight.TryGetProperty("fightPercentage", out var fpEl) ||
                                fpEl.ValueKind != JsonValueKind.Number)
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

                    if (bestFightPct.HasValue)
                    {
                        // Display value: boss HP % in the last phase reached, falling back to the fight %.
                        var displayPct = bestBossPct ?? bestFightPct.Value;
                        results[i] = new EncounterParseResult(true, true, 0, null, displayPct)
                        {
                            ProgLastPhase = bestPhase,
                        };
                    }
                }
            }
        }
        catch (Exception ex)
        {
            PassportCheckerReborn.Log.Warning(ex,
                "[FFLogsService] Failed to parse progression response.");
        }

        // Any no-kill player still without a result (no recent reports / no wipes on this encounter) → no logs,
        // except where a transient per-alias error means we couldn't actually tell — mark those retryable so
        // the next refresh re-queries instead of caching a wrong "No logs".
        foreach (var i in noKillIndices)
        {
            results.TryAdd(i, transientErrored.Contains(i)
                ? new EncounterParseResult(false, true, 0, null, null) { FetchFailed = true }
                : new EncounterParseResult(false, true, 0, null, null));
        }

        PublishNoKill();
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

    /// <summary>
    /// True when the lookup request itself failed (network error, rate limit, bad response) rather than the
    /// player genuinely having no logs. Lets the UI show "lookup failed" instead of a misleading "No logs".
    /// Failure results are never cached, so the next refresh retries.
    /// </summary>
    public bool FetchFailed { get; init; }

    /// <summary>
    /// For a no-kill player, the phase (lastPhase) reached on their best progression pull, so the UI can
    /// show e.g. "P4 · 3%". Null for single-phase fights or when phase data is unavailable.
    /// </summary>
    public int? ProgLastPhase { get; init; }

    /// <summary>Best parse on the player's current job for this encounter.</summary>
    public double? CurrentJobBestParse { get; init; }

    /// <summary>FFLogs spec name for the overall best parse (any job).</summary>
    public string? BestParseSpec { get; init; }

    /// <summary>FFXIV job abbreviation for the overall best parse job.</summary>
    public string? BestParseJobAbbreviation { get; init; }

    /// <summary>Icon ID for the overall best parse job.</summary>
    public uint? BestParseJobIconId { get; init; }
}
