using System.Collections.Generic;

namespace PassportCheckerReborn;

/// <summary>
/// Minimal in-code localization. UI strings are written inline in English and passed through
/// <see cref="T"/>; when the language is Korean and a translation exists it is substituted,
/// otherwise the original English falls through. Keys that contain an ImGui id suffix
/// (<c>label##id</c>) keep the same suffix in the translation so widget identity is preserved.
/// </summary>
public static class Loc
{
    public static PluginLanguage Language { get; set; } = PluginLanguage.English;

    /// <summary>Translates <paramref name="english"/> to the current language, or returns it unchanged.</summary>
    public static string T(string english)
        => Language == PluginLanguage.Korean && Korean.TryGetValue(english, out var v) ? v : english;

    private static readonly Dictionary<string, string> Korean = new()
    {
        // ── Language selector ────────────────────────────────────────────────
        ["Language"] = "언어",
        ["English"] = "English",
        ["Korean"] = "한국어",

        // ── Tabs ─────────────────────────────────────────────────────────────
        ["General"] = "일반",
        ["Overlay"] = "오버레이",
        ["FFLogs Integration"] = "FFLogs 연동",
        ["Tomestone Integration"] = "Tomestone 연동",
        ["PlayerTrack"] = "PlayerTrack",
        ["About"] = "정보",

        // ── General tab ──────────────────────────────────────────────────────
        ["Party Finder Detail Optimizations"] = "파티 찾기 상세 최적화",
        ["Keep the Party Finder window open when the party changes"] = "파티 구성이 바뀌어도 파티 찾기 창 유지",
        ["The game normally closes the Party Finder detail window when your party composition changes; this keeps it (and the member overlay) open."] = "게임은 파티 구성이 바뀌면 파티 찾기 상세 창을 자동으로 닫습니다. 이 옵션은 창(과 멤버 오버레이)을 계속 열어둡니다.",
        ["Show Party Job Icons"] = "직업 아이콘 표시",
        ["Party Finder List Optimizations"] = "파티 찾기 목록 최적화",
        ["Enable Automatic Refresh for Party Finder Listings"] = "파티 찾기 검색 결과 자동 새로 고침",
        ["Refresh Interval (seconds)##refresh"] = "새로고침 간격(초)##refresh",
        ["Right-Click Player Name to View Their Recruitment"] = "플레이어 이름 우클릭으로 모집 정보 보기",
        ["Adds a 'View Recruitment' option when you right-click a player. If they're hosting a Party Finder listing, it finds and opens it."]
            = "플레이어를 우클릭하면 메뉴에 '모집 정보 보기'가 추가됩니다. 그 사람이 파티 찾기 모집을 올렸다면 그 모집을 찾아서 열어줍니다.",
        ["View Recruitment"] = "모집 정보 보기",
        ["No active Party Finder listing found for this player."] = "이 플레이어의 활성 파티 찾기 모집을 찾지 못했습니다.",
        ["Unable to open Party Finder."] = "파티 찾기를 열 수 없습니다.",
        ["Listing by {0} not found on the current page. It may have expired or be on a different category/page."]
            = "{0}님의 모집을 현재 페이지에서 찾지 못했습니다. 만료됐거나 다른 카테고리/페이지에 있을 수 있습니다.",
        ["Blacklist"] = "차단 목록",
        ["Enable Blacklist Feature"] = "차단 목록 기능 사용",
        ["When enabled, players on your in-game blacklist are marked with [BL] in the overlay."]
            = "사용 시, 게임 내 차단 목록에 있는 플레이어가 오버레이에 [BL]로 표시됩니다.",
        ["Refresh##bl_refresh"] = "새로고침##bl_refresh",
        ["Re-reads the blacklist from the game and saves the result."] = "게임에서 차단 목록을 다시 읽어 저장합니다.",

        // ── Overlay tab ──────────────────────────────────────────────────────
        ["Show Member Info Overlay in PF Details"] = "파티 찾기 상세에 멤버 정보 오버레이 표시",
        ["Only Show Overlay for High-End Duties"] = "고난도 임무에서만 오버레이 표시",
        ["Show overlay on the left side"] = "오버레이를 왼쪽에 표시",
        ["Unchecked places the overlay on the right side of the Party Finder."] = "체크 해제 시 파티 찾기 오른쪽에 표시합니다.",
        ["Show Resolved Player Names in Member Info Overlay"] = "멤버 정보 오버레이에 실제 플레이어 이름 표시",
        ["When enabled, displays the actual player name (Name@World) once resolved, instead of \"Player X\"."]
            = "사용 시, 이름이 확인되면 \"Player X\" 대신 실제 이름(이름@서버)을 표시합니다.",
        ["Re-verify stale player names via adventure plate"] = "모험가 카드로 오래된 플레이어 이름 재확인",
        ["When a cached name is older than the threshold below, quietly re-checks it against the player's adventure plate. Throttled by the cooldown so the same stale name isn't re-checked constantly; detected renames are recorded in the name history."]
            = "캐시된 이름이 아래 기준보다 오래되면 플레이어의 모험가 카드로 조용히 다시 확인합니다. 쿨다운으로 제한되어 같은 이름을 계속 재확인하지 않으며, 감지된 닉네임 변경은 이름 기록에 남습니다.",
        ["Stale after (days)##stale_days"] = "오래됨 기준(일)##stale_days",
        ["Retry cooldown (hours)##reverify_cd"] = "재시도 쿨다운(시간)##reverify_cd",
        ["Re-check hidden (Private) players every (hours)##private_cd"] = "비공개 플레이어 재확인 주기(시간)##private_cd",
        ["How often to re-attempt an adventure-plate lookup for players whose plate is hidden. Higher = fewer wasted requests, but slower to notice if they make their plate public."]
            = "카드가 숨겨진 플레이어에게 카드 조회를 다시 시도하는 주기입니다. 값이 클수록 헛된 요청은 줄지만, 상대가 카드를 공개해도 알아채는 데 더 오래 걸립니다.",
        ["Enable FFLogs Integration (configure in FFLogs Integration tab)"]
            = "FFLogs 연동 사용 (FFLogs 연동 탭에서 설정)",
        ["Enable Tomestone Integration (configure API key in Tomestone Integration tab)"]
            = "Tomestone 연동 사용 (Tomestone 연동 탭에서 API 키 설정)",
        ["Tomestone.gg has no data for the Korean data centres, so it's unavailable on the Korean client."]
            = "Tomestone.gg는 한국 데이터센터를 지원하지 않아, 한국 클라이언트에서는 사용할 수 없습니다.",
        ["Party List Overlay"] = "파티 목록 오버레이",
        ["Show Info for Current Party Members"] = "현재 파티원 정보 표시",
        ["Shows FFLogs and Tomestone data for your current party members in an overlay attached to the Party Members UI element. Requires FFLogs Integration and/or Tomestone Integration to be enabled. Includes a duty selector dropdown for encounter-specific lookups."]
            = "현재 파티원의 FFLogs·Tomestone 데이터를 파티원 UI에 붙는 오버레이로 표시합니다. FFLogs 연동 또는 Tomestone 연동이 켜져 있어야 합니다. 특정 인카운터 조회를 위한 임무 선택 드롭다운이 포함됩니다.",
        ["Overlay Position:"] = "오버레이 위치:",
        ["Hide Party List Overlay while in a duty"] = "임무 중 파티 목록 오버레이 숨기기",
        ["Hide Party List Overlay while in combat"] = "전투 중 파티 목록 오버레이 숨기기",

        // ── FFLogs tab ───────────────────────────────────────────────────────
        ["FFLogs API Configuration"] = "FFLogs API 설정",
        ["Automatically look up FFLogs data once all names are resolved"] = "모든 이름이 확인되면 FFLogs 정보를 자동으로 조회",
        ["Runs a lookup automatically when every name resolves. Spends FFLogs API points (see below)."] = "모든 이름이 확인되면 자동으로 조회합니다. FFLogs API 포인트를 소모합니다 (아래 참고).",
        ["API Usage"] = "API 사용량",
        ["Checking…"] = "확인 중…",
        ["Refresh##fflogs_usage"] = "새로 고침##fflogs_usage",
        ["{0} / {1} points used this hour (resets in {2} min)"] = "이번 시간 {0} / {1} 포인트 사용 ({2}분 후 초기화)",
        ["Could not retrieve API usage."] = "API 사용량을 가져오지 못했습니다.",
        ["Client ID"] = "클라이언트 ID",
        ["Client Secret"] = "클라이언트 시크릿",
        ["Save & Test Credentials"] = "저장 및 자격 증명 테스트",
        ["Testing…"] = "테스트 중…",
        ["How to obtain FFLogs API credentials"] = "FFLogs API 자격 증명 발급 방법",
        ["1. Navigate to FFLogs API portal:"] = "1. FFLogs API 포털로 이동:",
        ["Open FFLogs API Portal"] = "FFLogs API 포털 열기",
        ["2. Click 'Create Client' in the top-right corner."] = "2. 우측 상단의 'Create Client'를 클릭.",
        ["3. Enter a client name (e.g. 'PassportCheckerReborn')."] = "3. 클라이언트 이름 입력 (예: 'PassportCheckerReborn').",
        ["Copy Client Name"] = "클라이언트 이름 복사",
        ["4. Provide any Redirect URL (e.g. 'https://example.com/')."] = "4. 아무 Redirect URL 입력 (예: 'https://example.com/').",
        ["Copy Redirect URL"] = "Redirect URL 복사",
        ["5. Leave 'Public Client' unchecked. "] = "5. 'Public Client'는 체크하지 않은 채로 둡니다. ",
        ["6. Copy the generated Client ID/Secret to the fields above. "] = "6. 생성된 Client ID/Secret을 위 칸에 복사합니다. ",
        ["7. Click 'Save & Test Credentials' to verify token status. "] = "7. 'Save & Test Credentials'를 눌러 토큰 상태를 확인합니다. ",
        ["Note: The Client Secret is only shown once. Keep it private."] = "참고: Client Secret은 한 번만 표시됩니다. 유출되지 않게 보관하세요.",

        // ── Tomestone tab ────────────────────────────────────────────────────
        ["Tomestone API Configuration"] = "Tomestone API 설정",
        ["When enabled, clicking 'Tomestone' in the overlay will fetch prog point and activity data for the current duty from the Tomestone.gg API."]
            = "사용 시, 오버레이에서 'Tomestone'을 클릭하면 Tomestone.gg API에서 현재 임무의 진행도·활동 데이터를 가져옵니다.",
        ["API Key (Bearer token)"] = "API 키 (Bearer 토큰)",
        ["Save##ts_save"] = "저장##ts_save",
        ["How to obtain a Tomestone API key"] = "Tomestone API 키 발급 방법",
        ["1. Navigate to Tomestone Account Settings:"] = "1. Tomestone 계정 설정으로 이동:",
        ["Open Tomestone Account Page"] = "Tomestone 계정 페이지 열기",
        ["2. Scroll down to the \"API access token\" section."] = "2. \"API access token\" 항목까지 스크롤합니다.",
        ["3. Click \"Generate access token\"."] = "3. \"Generate access token\"을 클릭합니다.",
        ["4. Copy the generated token and paste it into the field above."] = "4. 생성된 토큰을 복사해 위 칸에 붙여넣습니다.",
        ["5. Click 'Save' to store your API key."] = "5. 'Save'를 눌러 API 키를 저장합니다.",
        ["Note: Keep your API token private. It grants access to your Tomestone account data."]
            = "참고: API 토큰은 유출되지 않게 보관하세요. Tomestone 계정 데이터에 접근할 수 있습니다.",

        // ── PlayerTrack tab ──────────────────────────────────────────────────
        ["PlayerTrack Integration"] = "PlayerTrack 연동",
        ["When enabled, party members whose name can't be read from Party Finder packets or the adventure plate are looked up in the PlayerTrack plugin's local database (read-only). This can recover names of players who hide their adventure plate, as long as you have encountered them before."]
            = "사용 시, 파티 찾기 패킷이나 모험가 카드로 이름을 읽을 수 없는 파티원을 PlayerTrack 플러그인의 로컬 DB에서 조회합니다(읽기 전용). 이전에 마주친 적이 있다면, 모험가 카드를 숨긴 플레이어의 이름도 복원할 수 있습니다.",
        ["Status:"] = "상태:",
        ["PlayerTrack plugin installed"] = "PlayerTrack 플러그인 설치됨",
        ["PlayerTrack plugin not found"] = "PlayerTrack 플러그인 없음",
        ["PlayerTrack is loaded"] = "PlayerTrack 로드됨",
        ["PlayerTrack not currently loaded"] = "PlayerTrack 현재 로드 안 됨",
        ["Database found"] = "데이터베이스 찾음",
        ["Database not found"] = "데이터베이스 없음",
        ["Integration is inactive: the PlayerTrack database was not found."] = "PlayerTrack 데이터베이스를 찾지 못해 연동이 비활성 상태입니다.",
        ["Install and run PlayerTrack at least once so it builds its database, then reopen this window."]
            = "PlayerTrack을 설치하고 최소 한 번 실행해 DB를 생성한 뒤, 이 창을 다시 열어 주세요.",
        ["Enable PlayerTrack name resolution"] = "PlayerTrack 이름 조회 사용",
        ["Reads PlayerTrack's database (read-only) to resolve otherwise-unknown party member names."]
            = "PlayerTrack 데이터베이스를 읽어(읽기 전용) 알 수 없는 파티원 이름을 조회합니다.",
        ["Resolution priority:"] = "조회 우선순위:",
        ["Adventure Plate first (freshest)"] = "모험가 카드 우선 (가장 최신)",
        ["PlayerTrack first (fastest)"] = "PlayerTrack 우선 (가장 빠름)",
        ["Tries the live adventure plate first (most up-to-date name). If the plate is hidden or the lookup fails, falls back to PlayerTrack."]
            = "실시간 모험가 카드를 먼저 조회합니다(가장 최신 이름). 카드가 숨겨져 있거나 조회에 실패하면 PlayerTrack으로 대체합니다.",
        ["Uses PlayerTrack's stored name first (instant, no network request, works for hidden plates). Only queries the adventure plate when PlayerTrack has no record. Note: PlayerTrack data can be stale if the player has since renamed or transferred worlds."]
            = "PlayerTrack에 저장된 이름을 먼저 사용합니다(즉시, 네트워크 요청 없음, 숨긴 카드에도 동작). PlayerTrack에 기록이 없을 때만 모험가 카드를 조회합니다. 참고: 플레이어가 이후 닉네임을 변경하거나 서버를 이전했다면 PlayerTrack 데이터가 오래됐을 수 있습니다.",
        ["Names resolved via PlayerTrack are marked with a [PT] tag in the overlay. Hover a member's name (or the tag) to see the name's source, how old the cached data is, and any previous names."]
            = "PlayerTrack으로 조회한 이름은 오버레이에 [PT] 태그로 표시됩니다. 멤버의 이름(또는 태그)에 마우스를 올리면 이름 출처, 캐시 데이터의 오래됨 정도, 이전 이름을 볼 수 있습니다.",

        // ── About tab ────────────────────────────────────────────────────────
        ["Passport Checker Reborn"] = "Passport Checker Reborn",
        ["An open-source Party Finder enhancement plugin for Final Fantasy XIV."]
            = "파이널 판타지 14용 오픈소스 파티 찾기 강화 플러그인입니다.",
        ["Author:  The Combat Reborn Team - LTS"] = "제작:  The Combat Reborn Team - LTS",
        ["Passport Checker Reborn is an open-source alternative to the PFFinder plugin. It shows a member-info overlay alongside party finder listings, integrates with Tomestone.gg and FFLogs for quick prog-point lookups, and offers quality-of-life improvements to the party finder UI."]
            = "Passport Checker Reborn은 PFFinder 플러그인의 오픈소스 대안입니다. 파티 찾기 모집 옆에 멤버 정보 오버레이를 표시하고, Tomestone.gg 및 FFLogs와 연동해 빠른 진행도 조회를 제공하며, 파티 찾기 UI에 편의 기능을 더합니다.",
        ["Commands:\n  /pfchecker (or /pcr)  – Open the settings window.\n  /pcrparty  – Toggle the party list overlay window.\n"]
            = "명령어:\n  /pfchecker (또는 /pcr)  – 설정 창 열기.\n  /pcrparty  – 파티 목록 오버레이 창 토글.\n",
        ["Cache Statistics"] = "캐시 통계",
        ["Hide this party-list overlay. Re-enable it in Settings → Overlay → Party List Overlay."] = "이 파티 목록 오버레이를 숨깁니다. 설정 → 오버레이 → 파티 목록 오버레이에서 다시 켤 수 있습니다.",
        ["This integration is off. Turn it on in the Overlay tab to show FFLogs in the overlay."] = "이 연동은 꺼져 있습니다. 오버레이 탭에서 켜면 오버레이에 FFLogs가 표시됩니다.",
        ["This integration is off. Turn it on in the Overlay tab to show Tomestone in the overlay."] = "이 연동은 꺼져 있습니다. 오버레이 탭에서 켜면 오버레이에 Tomestone이 표시됩니다.",
        ["Overlay markers"] = "오버레이 표식",
        ["Name recovered from the PlayerTrack database"] = "PlayerTrack DB에서 복원한 이름",
        ["On your in-game blacklist"] = "게임 차단 목록에 있음",
        ["Adventure plate is hidden"] = "모험가 카드 비공개",
        ["FFLogs request failed — refresh to retry"] = "FFLogs 조회 실패 — 새로고침하여 재시도",
        ["Resolved CIDs"] = "확인된 CID 수",
        ["Blacklisted players"] = "차단 목록 인원",
        ["Clear Cache##bl_clear"] = "캐시 비우기##bl_clear",
        ["Clears the persisted blacklist cache, then re-reads from the game."] = "저장된 차단 목록 캐시를 비운 뒤 게임에서 다시 읽습니다.",
        ["Clear Cache##cid_clear"] = "캐시 비우기##cid_clear",
        ["Deletes all stored Content ID → name/world mappings and their name history from disk. Names are re-learned as you encounter players again.\nHold SHIFT and click to enable this button."]
            = "저장된 모든 콘텐츠 ID → 이름/서버 매핑과 이름 기록을 디스크에서 삭제합니다. 플레이어를 다시 만나면 이름이 재학습됩니다.\nSHIFT를 누른 채 클릭하면 버튼이 활성화됩니다.",
        ["Clear cached names?"] = "저장된 이름을 삭제할까요?",
        ["This permanently deletes all {0} stored names and their history. This cannot be undone."]
            = "저장된 이름 {0}개와 이름 기록을 영구히 삭제합니다. 되돌릴 수 없습니다.",
        ["Delete"] = "삭제",
        ["Cancel"] = "취소",

        // ── PF overlay window ────────────────────────────────────────────────
        ["No party finder listing selected."] = "선택된 파티 찾기 모집이 없습니다.",
        ["Open a PF detail window to see member info."] = "파티 찾기 상세 창을 열면 멤버 정보가 표시됩니다.",
        ["Not a high-end duty."] = "고난도 임무가 아닙니다.",
        ["PF Member Info"] = "파티 찾기 멤버 정보",
        ["Tomestone API Key Needed##ts_all"] = "Tomestone API 키 필요##ts_all",
        ["Configure your Tomestone API key in Settings → Tomestone Integration."]
            = "설정 → Tomestone 연동에서 Tomestone API 키를 설정하세요.",
        ["FFLogs API Key Needed##ff_all"] = "FFLogs API 키 필요##ff_all",
        ["Configure your FFLogs credentials in Settings → FFLogs Integration."]
            = "설정 → FFLogs 연동에서 FFLogs 자격 증명을 설정하세요.",
        ["Waiting for player names to be resolved…"] = "플레이어 이름 확인 중…",
        ["Data age"] = "데이터 나이",
        ["Looking up Tomestone data for all players…"] = "모든 플레이어의 Tomestone 데이터 조회 중…",
        ["Look up Tomestone data for all players"] = "모든 플레이어의 Tomestone 데이터 조회",
        ["Looking up FFLogs data for all players…"] = "모든 플레이어의 FFLogs 데이터 조회 중…",
        ["Look up FFLogs data for all players"] = "모든 플레이어의 FFLogs 데이터 조회",
        ["FFLogs Lookup##ff_all"] = "FFLogs 조회##ff_all",
        ["FFLogs data already loaded for this listing"] = "이 모집의 FFLogs 데이터를 이미 불러왔습니다",
        ["Tomestone Lookup##ts_all"] = "Tomestone 조회##ts_all",
        ["Tomestone data already loaded for this listing"] = "이 모집의 Tomestone 데이터를 이미 불러왔습니다",
        ["Lookup failed"] = "조회 실패",
        ["Click to open FFLogs page"] = "클릭하여 FFLogs 페이지 열기",
        ["FFLogs lookup failed (network or rate limit) — refresh to retry."] = "FFLogs 조회 실패 (네트워크 또는 요청 한도) — 새로고침하여 재시도.",
        ["Player"] = "플레이어",
        ["[Private]"] = "[비공개]",
        ["Adventure plate is hidden or unavailable"] = "모험가 카드가 숨겨져 있거나 사용할 수 없습니다",
        ["On your blacklist"] = "내 차단 목록에 있음",
        ["No Logs"] = "기록 없음",
        ["No logs"] = "기록 없음",
        // FFLogs result formats ({0}=kills, {1}/{2}=parse%) — templates so word order localizes.
        ["Cleared {0}X"] = "{0}킬",
        ["{0}% wipe"] = "{0}% 전멸",
        ["P1 No logs"] = "P1 기록 없음",
        ["P2 No logs"] = "P2 기록 없음",
        ["Average overall parse {0}%"] = "최근 영식 평균 {0}%",
        ["Hidden Profile"] = "비공개 프로필",
        ["N/A"] = "해당 없음",
        ["Name"] = "이름",
        ["Job"] = "직업",
        ["This name is old and may be out of date."] = "이 이름은 오래되어 현재와 다를 수 있습니다.",
        ["Previously seen as:"] = "이전 이름:",
        // Relative time ({0}=number) and previous-name date prefix.
        ["just now"] = "방금 전",
        ["{0}m ago"] = "{0}분 전",
        ["{0}h ago"] = "{0}시간 전",
        ["{0}d ago"] = "{0}일 전",
        ["{0}y ago"] = "{0}년 전",
        ["until {0}"] = "{0}까지",

        // ── Party list overlay window ────────────────────────────────────────
        ["Party Member Info"] = "파티원 정보",
        ["Hide"] = "숨기기",
        ["Waiting for party data…"] = "파티 데이터 대기 중…",
        ["Duty:"] = "임무:",
        ["(None)"] = "(없음)",
        ["Loading..."] = "불러오는 중...",
        ["Fetching more data…"] = "데이터 더 가져오는 중…",
        ["Loading FFLogs & Tomestone data…"] = "FFLogs & Tomestone 데이터 불러오는 중…",
        ["Loading FFLogs data…"] = "FFLogs 데이터 불러오는 중…",
        ["Loading Tomestone data…"] = "Tomestone 데이터 불러오는 중…",
        ["Cleared"] = "클리어",

        // ── Chat ─────────────────────────────────────────────────────────────
        ["Party List Overlay shown"] = "파티 목록 오버레이 표시됨",
        ["Party List Overlay hidden"] = "파티 목록 오버레이 숨김",
    };
}
