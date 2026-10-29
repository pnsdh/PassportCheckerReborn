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
        // ── Language ─────────────────────────────────────────────────────────
        ["Language"] = "언어",
        ["English"] = "English",
        ["Korean"] = "한국어",

        // ── Navigation / page headings ───────────────────────────────────────
        ["General"] = "일반",
        ["Overlays"] = "오버레이",
        ["Appearance"] = "화면",
        ["About"] = "정보",
        ["Party Finder quality-of-life options"] = "파티 찾기 편의 기능",
        ["Member info and party list overlays"] = "멤버 정보·파티 목록 오버레이",
        ["What the member info and party list overlays show"] = "멤버 정보·파티 목록 오버레이에 표시할 내용",
        ["FFLogs integration and API client"] = "FFLogs 연동 및 API 클라이언트",
        ["Clears and parses from FFLogs"] = "FFLogs의 킬 수와 로그",
        ["Tomestone.gg integration and API key"] = "Tomestone.gg 연동 및 API 키",
        ["Prog points and clears from Tomestone.gg"] = "Tomestone.gg의 진행도와 클리어 기록",
        ["Resolve hidden names from PlayerTrack"] = "PlayerTrack으로 숨겨진 이름 확인",
        ["Language, text size and accent color"] = "언어, 글자 크기, 강조 색상",
        ["Language and how the plugin's windows look"] = "언어 및 플러그인 창 모양",
        ["Commands, caches and links"] = "명령어, 캐시, 링크",
        ["About Passport Checker Reborn"] = "Passport Checker Reborn 정보",
        ["Installed version"] = "설치된 버전",

        // ── Common ───────────────────────────────────────────────────────────
        ["Integration"] = "연동",
        ["Refresh"] = "새로 고침",
        ["Save"] = "저장",
        ["Open"] = "열기",
        ["Copy"] = "복사",
        ["Copied"] = "복사됨",
        ["Clear"] = "비우기",
        ["Close"] = "닫기",
        ["Delete"] = "삭제",
        ["Cancel"] = "취소",
        ["Unsaved changes."] = "저장되지 않은 변경 사항이 있습니다.",
        ["Clear search"] = "검색어 지우기",
        // Strings built into the RebornMaterial UI library (passed in via M3Fork.Localize).
        ["Hold to confirm"] = "길게 눌러 확인",
        ["Minimize"] = "최소화",
        ["Restore"] = "복원",
        ["Reset to default"] = "기본값으로 되돌리기",
        ["Search"] = "검색",
        ["No matches"] = "일치하는 항목 없음",
        ["Nothing to pick"] = "선택할 항목 없음",
        ["Reset"] = "초기화",
        ["Press a key"] = "키를 누르세요",
        ["None"] = "없음",

        // ── General page ─────────────────────────────────────────────────────
        ["Listing details"] = "모집 상세",
        ["Show job icons"] = "직업 아이콘 표시",
        ["Shows each player's job icon in the member info and party list overlays."]
            = "멤버 정보·파티 목록 오버레이에 각 플레이어의 직업 아이콘을 표시합니다.",
        ["Keep the Party Finder window open when the party changes"] = "파티 구성이 바뀌어도 파티 찾기 창 유지",
        ["Stops the Party Finder closing itself when your party changes, and reloads the listing you were viewing."]
            = "파티 구성이 바뀌어도 파티 찾기 창이 닫히지 않게 하고, 보던 모집을 다시 불러옵니다.",
        ["Unavailable: a game update changed what this setting relies on. It will return once the plugin is updated."]
            = "사용 불가: 게임 업데이트로 이 설정이 의존하는 부분이 바뀌었습니다. 플러그인이 업데이트되면 다시 사용할 수 있습니다.",
        ["Listings"] = "모집 목록",
        ["True time-based sorting"] = "남은 시간 순 정렬",
        ["Orders each page by the time listings have left, instead of grouping them by duty."] = "임무별로 묶는 대신, 각 페이지의 모집을 남은 시간 순으로 정렬합니다.",
        ["Order"] = "정렬 순서",
        ["Newest first"] = "최신 순",
        ["Oldest first"] = "오래된 순",
        ["The Party Finder's own sort button flips it."] = "파티 찾기의 정렬 버튼을 누르면 순서가 반대로 바뀝니다.",
        ["Show 100 listings per page"] = "페이지당 모집 100개 표시",
        ["Asks the server for 100 listings at a time instead of 50."] = "서버에 모집을 50개 대신 100개씩 요청합니다.",
        ["One-click job filter button"] = "원클릭 직업 필터 버튼",
        ["Adds a button above the Party Finder that hides high-end duty listings with no open slot for your current job."] = "파티 찾기 위에 버튼을 추가합니다. 누르면 현재 직업으로 들어갈 빈자리가 없는 고난도 임무 모집을 숨깁니다.",
        ["Job filter"] = "직업 필터",
        ["Open {0} slots only"] = "{0} 빈자리만",
        ["Filter for {0}"] = "{0} 빈자리만 보기",
        ["Switch to a combat class or job to filter listings."] = "전투 직업으로 바꾸면 모집을 필터링할 수 있습니다.",
        ["Hiding high-end duty listings with no open slot for {0}. Click to show them again."] = "{0} 빈자리가 없는 고난도 임무 모집을 숨기는 중입니다. 클릭하면 다시 표시합니다.",
        ["Hide high-end duty listings with no open slot for {0}."] = "{0} 빈자리가 없는 고난도 임무 모집을 숨깁니다.",
        ["Refresh listings automatically"] = "파티 찾기 목록 자동 새로 고침",
        ["Reloads the Party Finder list on a timer while you're browsing it."]
            = "파티 찾기 목록을 보는 동안 일정 간격으로 다시 불러옵니다.",
        ["Refresh interval"] = "새로 고침 간격",
        [" s"] = "초",
        ["Right-Click Player Name to View Their Recruitment"] = "플레이어 이름 우클릭으로 모집 정보 보기",
        ["Adds a 'View Recruitment' option when you right-click a player. If they're hosting a Party Finder listing, it finds and opens it."]
            = "플레이어를 우클릭하면 메뉴에 '모집 정보 보기'가 추가됩니다. 그 사람이 파티 찾기 모집을 올렸다면 그 모집을 찾아서 열어줍니다.",
        ["View Recruitment"] = "모집 정보 보기",
        ["Unable to request this player's Party Finder listing."] = "이 플레이어의 파티 찾기 모집을 요청할 수 없습니다.",
        ["Blacklist"] = "차단 목록",
        ["Flag blacklisted players"] = "차단 목록 플레이어 표시",
        ["Marks players on your in-game blacklist with a BL tag in the member info overlay."]
            = "게임 내 차단 목록에 있는 플레이어를 멤버 정보 오버레이에 BL 태그로 표시합니다.",
        ["Refresh blacklist"] = "차단 목록 새로 고침",
        ["Re-reads the blacklist from the game and saves the result."] = "게임에서 차단 목록을 다시 읽어 저장합니다.",

        // ── Overlays page ────────────────────────────────────────────────────
        ["Member info overlay"] = "멤버 정보 오버레이",
        ["Appears beside a Party Finder listing's details."] = "파티 찾기 모집 상세 창 옆에 표시됩니다.",
        ["Show member info overlay"] = "멤버 정보 오버레이 표시",
        ["High-end duties only"] = "고난도 임무에서만 표시",
        ["Hides the overlay for listings that aren't high-end duties."] = "고난도 임무가 아닌 모집에서는 오버레이를 숨깁니다.",
        ["Side"] = "표시 방향",
        ["Which side of the listing details the overlay sits on."] = "오버레이를 모집 상세 창의 어느 쪽에 둘지 정합니다.",
        ["Left"] = "왼쪽",
        ["Right"] = "오른쪽",
        ["Above"] = "위",
        ["Below"] = "아래",
        ["Unbound"] = "자유 배치",
        ["Show resolved player names"] = "확인된 플레이어 이름 표시",
        ["Shows Name@World once a player is resolved, instead of \"Player 1\"."]
            = "이름이 확인되면 \"플레이어 1\" 대신 이름@서버를 표시합니다.",
        ["Known players"] = "아는 플레이어",
        ["Highlights players you know in the member info overlay."] = "멤버 정보 오버레이에서 아는 플레이어를 강조합니다.",
        ["{0} remembered for this character"] = "이 캐릭터 기준 {0}명 기억 중",
        ["Friends"] = "친구",
        ["Free company members"] = "자유부대원",
        ["Linkshell members"] = "링크셸 멤버",
        ["Cross-world linkshell members"] = "크로스월드 링크셸 멤버",
        ["Highlight color"] = "강조 색상",
        ["Forget remembered linkshell members"] = "기억한 링크셸 멤버 비우기",
        ["Linkshell lists are added to as you view each shell, so people who left stay listed until cleared."] = "링크셸 명단은 셸을 볼 때마다 누적되므로, 탈퇴한 사람도 비우기 전까지 남아 있습니다.",
        ["The game only loads these lists when you open their window (Friend List, Free Company, Linkshell, Cross-world Linkshell). Open each once and they are remembered for this character."] = "게임은 이 명단들을 해당 창(친구 목록, 자유부대, 링크셸, 크로스월드 링크셸)을 열 때만 불러옵니다. 한 번씩 열어 두면 이 캐릭터 기준으로 기억합니다.",
        ["Known player"] = "아는 플레이어",
        ["Friend, free company or linkshell member (the whole row is tinted)"] = "친구, 자유부대원, 링크셸 멤버 (줄 전체가 강조됨)",
        ["Friend"] = "친구",
        ["Free Company"] = "자유부대",
        ["Linkshell"] = "링크셸",
        ["Cross-world Linkshell"] = "크로스월드 링크셸",
        ["Name freshness"] = "이름 최신화",
        ["Keeps cached player names up to date."] = "저장된 플레이어 이름을 최신 상태로 유지합니다.",
        ["Re-verify stale player names via adventure plate"] = "모험가 카드로 오래된 플레이어 이름 재확인",
        ["When a cached name is older than the threshold below, quietly re-checks it against the player's adventure plate. Throttled by the cooldown so the same stale name isn't re-checked constantly; detected renames are recorded in the name history."]
            = "캐시된 이름이 아래 기준보다 오래되면 플레이어의 모험가 카드로 조용히 다시 확인합니다. 쿨다운으로 제한되어 같은 이름을 계속 재확인하지 않으며, 감지된 닉네임 변경은 이름 기록에 남습니다.",
        ["Stale after"] = "오래됨 기준",
        [" days"] = "일",
        ["Retry cooldown"] = "재시도 쿨다운",
        [" h"] = "시간",
        ["Re-check hidden (Private) players every"] = "비공개 플레이어 재확인 주기",
        ["How often to re-attempt an adventure-plate lookup for players whose plate is hidden. Higher = fewer wasted requests, but slower to notice if they make their plate public."]
            = "카드가 숨겨진 플레이어에게 카드 조회를 다시 시도하는 주기입니다. 값이 클수록 헛된 요청은 줄지만, 상대가 카드를 공개해도 알아채는 데 더 오래 걸립니다.",
        ["The party list overlay stays hidden until FFLogs or Tomestone is turned on."]
            = "FFLogs 또는 Tomestone 연동을 켜야 파티 목록 오버레이가 표시됩니다.",
        ["Party list overlay"] = "파티 목록 오버레이",
        ["FFLogs and Tomestone data for your current party, next to the party list. Includes a duty picker for encounter-specific lookups."]
            = "현재 파티원의 FFLogs·Tomestone 데이터를 파티 목록 옆에 표시합니다. 특정 임무 기록을 조회할 수 있는 임무 선택 목록이 포함됩니다.",
        ["Show party list overlay"] = "파티 목록 오버레이 표시",
        ["You can also toggle it with /pcrparty."] = "/pcrparty 명령어로도 켜고 끌 수 있습니다.",
        ["Position"] = "위치",
        ["Where the overlay sits relative to the party list. Unbound lets you drag it anywhere."]
            = "파티 목록 기준 오버레이 위치입니다. 자유 배치를 고르면 원하는 곳으로 옮길 수 있습니다.",
        ["Hide in duties"] = "임무 중 숨기기",
        ["Hide in combat"] = "전투 중 숨기기",

        // ── FFLogs page ──────────────────────────────────────────────────────
        ["FFLogs is turned on, but no API client is saved yet. Add one below."]
            = "FFLogs 연동이 켜져 있지만 저장된 API 클라이언트가 없습니다. 아래에서 추가하세요.",
        ["Show FFLogs data in overlays"] = "오버레이에 FFLogs 데이터 표시",
        ["Adds an FFLogs column with clears and parses to the member info and party list overlays."]
            = "멤버 정보·파티 목록 오버레이에 킬 수와 로그를 보여주는 FFLogs 열을 추가합니다.",
        ["Automatically look up FFLogs data once all names are resolved"] = "모든 이름이 확인되면 FFLogs 정보를 자동으로 조회",
        ["Runs a lookup automatically when every name resolves. Spends FFLogs API points (see below)."]
            = "모든 이름이 확인되면 자동으로 조회합니다. FFLogs API 포인트를 소모합니다 (아래 참고).",
        ["API client"] = "API 클라이언트",
        ["Create a client on FFLogs, then paste its ID and secret here."] = "FFLogs에서 클라이언트를 만든 뒤 ID와 시크릿을 여기에 붙여넣으세요.",
        ["Client ID"] = "클라이언트 ID",
        ["Client Secret"] = "클라이언트 시크릿",
        ["Paste your client ID"] = "클라이언트 ID 붙여넣기",
        ["Paste your client secret"] = "클라이언트 시크릿 붙여넣기",
        ["Save & test"] = "저장 및 테스트",
        ["Testing…"] = "테스트 중…",
        ["Checking your credentials with FFLogs…"] = "FFLogs에서 자격 증명 확인 중…",
        ["FFLogs accepted these credentials."] = "FFLogs에서 자격 증명이 확인되었습니다.",
        ["FFLogs rejected these credentials."] = "FFLogs에서 자격 증명이 거부되었습니다.",
        ["The test failed: {0}"] = "테스트 실패: {0}",
        ["API Usage"] = "API 사용량",
        ["Points this hour"] = "이번 시간 사용량",
        ["Checking…"] = "확인 중…",
        ["{0} / {1} points used this hour (resets in {2} min)"] = "이번 시간 {0} / {1} 포인트 사용 ({2}분 후 초기화)",
        ["Could not retrieve API usage."] = "API 사용량을 가져오지 못했습니다.",
        ["How to get API credentials"] = "API 자격 증명 발급 방법",
        ["Open the FFLogs API clients page."] = "FFLogs API 클라이언트 페이지를 엽니다.",
        ["Click \"Create Client\" in the top-right corner."] = "우측 상단의 \"Create Client\"를 클릭합니다.",
        ["Enter a client name, such as {0}."] = "클라이언트 이름을 입력합니다. 예: {0}",
        ["Enter any redirect URL, such as {0}"] = "Redirect URL을 아무거나 입력합니다. 예: {0}",
        ["Leave \"Public Client\" unchecked."] = "\"Public Client\"는 체크하지 않은 채로 둡니다.",
        ["Copy the generated client ID and secret into the fields above."] = "생성된 클라이언트 ID와 시크릿을 위 칸에 붙여넣습니다.",
        ["Click Save & test to check them."] = "저장 및 테스트를 눌러 확인합니다.",
        ["The client secret is only shown once. Keep it private."] = "클라이언트 시크릿은 한 번만 표시됩니다. 유출되지 않게 보관하세요.",

        // ── Tomestone page ───────────────────────────────────────────────────
        ["Tomestone is turned on, but no API key is saved yet. Add one below."]
            = "Tomestone 연동이 켜져 있지만 저장된 API 키가 없습니다. 아래에서 추가하세요.",
        ["Show Tomestone data in overlays"] = "오버레이에 Tomestone 데이터 표시",
        ["Adds a Tomestone column with prog points and clears. In the member info overlay, click Tomestone to look up the listing's duty."]
            = "진행도와 클리어 기록을 보여주는 Tomestone 열을 추가합니다. 멤버 정보 오버레이에서 Tomestone 조회를 누르면 해당 모집의 임무를 조회합니다.",
        ["API access"] = "API 접근",
        ["Your Tomestone.gg access token, sent with each lookup."] = "조회할 때마다 함께 보내는 Tomestone.gg 액세스 토큰입니다.",
        ["API key"] = "API 키",
        ["Paste your access token"] = "액세스 토큰 붙여넣기",
        ["API key saved."] = "API 키가 저장되었습니다.",
        ["How to get an API key"] = "API 키 발급 방법",
        ["Open your Tomestone account settings."] = "Tomestone 계정 설정을 엽니다.",
        ["Scroll down to the \"API access token\" section."] = "\"API access token\" 항목까지 스크롤합니다.",
        ["Click \"Generate access token\"."] = "\"Generate access token\"을 클릭합니다.",
        ["Paste the token into the field above."] = "토큰을 위 칸에 붙여넣습니다.",
        ["Click Save."] = "저장을 누릅니다.",
        ["Keep your token private. It grants access to your Tomestone account data."]
            = "토큰은 유출되지 않게 보관하세요. Tomestone 계정 데이터에 접근할 수 있습니다.",

        // ── PlayerTrack page ─────────────────────────────────────────────────
        ["Status"] = "상태",
        ["When enabled, party members whose name can't be read from Party Finder packets or the adventure plate are looked up in the PlayerTrack plugin's local database (read-only). This can recover names of players who hide their adventure plate, as long as you have encountered them before."]
            = "사용 시, 파티 찾기 패킷이나 모험가 카드로 이름을 읽을 수 없는 파티원을 PlayerTrack 플러그인의 로컬 DB에서 조회합니다(읽기 전용). 이전에 마주친 적이 있다면, 모험가 카드를 숨긴 플레이어의 이름도 복원할 수 있습니다.",
        ["PlayerTrack plugin installed"] = "PlayerTrack 플러그인 설치됨",
        ["PlayerTrack plugin not found"] = "PlayerTrack 플러그인 없음",
        ["PlayerTrack is loaded"] = "PlayerTrack 로드됨",
        ["PlayerTrack not currently loaded"] = "PlayerTrack 현재 로드 안 됨",
        ["Database found"] = "데이터베이스 찾음",
        ["Database not found"] = "데이터베이스 없음",
        ["Integration is inactive: the PlayerTrack database was not found."] = "PlayerTrack 데이터베이스를 찾지 못해 연동이 비활성 상태입니다.",
        ["Install and run PlayerTrack at least once so it builds its database, then reopen this window."]
            = "PlayerTrack을 설치하고 최소 한 번 실행해 DB를 생성한 뒤, 이 창을 다시 열어 주세요.",
        ["Name resolution"] = "이름 확인",
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
        ["Names resolved via PlayerTrack are marked with a PT tag in the overlay. Hover a member's name (or the tag) to see the name's source, how old the cached data is, and any previous names."]
            = "PlayerTrack으로 조회한 이름은 오버레이에 PT 태그로 표시됩니다. 멤버의 이름(또는 태그)에 마우스를 올리면 이름 출처, 캐시 데이터의 오래됨 정도, 이전 이름을 볼 수 있습니다.",

        // ── Appearance page ──────────────────────────────────────────────────
        ["Text size"] = "글자 크기",
        ["Size"] = "크기",
        ["Element size"] = "요소 크기",
        ["Size of buttons, switches and other controls."] = "버튼, 스위치 등 컨트롤의 크기입니다.",
        ["Spacing"] = "여백",
        ["Padding and gaps between controls, on top of the element size."] = "컨트롤 안팎의 여백과 간격입니다. 요소 크기에 더해 적용됩니다.",
        ["Size of the text in the plugin's windows, relative to Dalamud's default font."] = "플러그인 창의 글자 크기입니다. Dalamud 기본 글꼴 크기가 기준입니다.",
        ["Theme"] = "테마",
        ["Every color in the plugin's windows is generated from one accent color."] = "플러그인 창의 모든 색상은 하나의 강조 색상에서 만들어집니다.",
        ["Accent color"] = "강조 색상",
        ["Very dark or grey colors fall back to the default."] = "너무 어둡거나 회색에 가까운 색은 기본값으로 대체됩니다.",
        ["Reset to the default accent"] = "기본 강조 색상으로 되돌리기",
        ["Presets"] = "프리셋",
        ["Crimson (default)"] = "크림슨 (기본)",
        ["Sapphire"] = "사파이어",
        ["Teal"] = "청록",
        ["Forest"] = "포레스트",
        ["Amber"] = "앰버",
        ["Violet"] = "바이올렛",
        ["Rose"] = "로즈",

        // ── About page ───────────────────────────────────────────────────────
        ["Passport Checker Reborn"] = "Passport Checker Reborn",
        ["Passport Checker Reborn is an open-source alternative to the PFFinder plugin. It shows a member-info overlay alongside party finder listings, integrates with Tomestone.gg and FFLogs for quick prog-point lookups, and offers quality-of-life improvements to the party finder UI."]
            = "Passport Checker Reborn은 PFFinder 플러그인의 오픈소스 대안입니다. 파티 찾기 모집 옆에 멤버 정보 오버레이를 표시하고, Tomestone.gg 및 FFLogs와 연동해 빠른 진행도 조회를 제공하며, 파티 찾기 UI에 편의 기능을 더합니다.",
        ["Support the original developers on Ko-fi"] = "Ko-fi로 원작자 후원하기",
        ["Goes to the original Passport Checker Reborn team (The Combat Reborn Team), not to the maintainer of this Custom version."]
            = "원작 Passport Checker Reborn 개발팀(The Combat Reborn Team)에게 가는 후원입니다. 이 커스텀 버전 관리자에게 가는 후원이 아닙니다.",
        ["Support the original developers on Ko-fi (The Combat Reborn Team, not this Custom version)"]
            = "Ko-fi로 원작자 후원하기 (원작 개발팀 The Combat Reborn Team, 이 커스텀 버전 아님)",
        ["Source code"] = "소스 코드",
        ["Contact (Discord)"] = "문의 (디스코드)",
        ["Commands"] = "명령어",
        ["Opens or closes this window. /pfchecker does the same."] = "이 창을 열거나 닫습니다. /pfchecker도 같습니다.",
        ["Shows or hides the party list overlay."] = "파티 목록 오버레이를 켜거나 끕니다.",
        ["Overlay markers"] = "오버레이 표식",
        ["Name recovered from the PlayerTrack database"] = "PlayerTrack DB에서 복원한 이름",
        ["On your in-game blacklist"] = "게임 차단 목록에 있음",
        ["Adventure plate is hidden"] = "모험가 카드 비공개",
        ["FFLogs request failed — refresh to retry"] = "FFLogs 조회 실패 — 새로고침하여 재시도",
        ["Caches"] = "캐시",
        ["Resolved players"] = "확인된 플레이어",
        ["Content IDs already matched to a name and world."] = "이름·서버가 확인된 콘텐츠 ID 수입니다.",
        ["Deletes all stored Content ID → name/world mappings and their name history from disk. Names are re-learned as you encounter players again.\nHold SHIFT and click to enable this button."]
            = "저장된 모든 콘텐츠 ID → 이름/서버 매핑과 이름 기록을 디스크에서 삭제합니다. 플레이어를 다시 만나면 이름이 재학습됩니다.\nSHIFT를 누른 채 클릭하면 버튼이 활성화됩니다.",
        ["Blacklisted players"] = "차단 목록 인원",
        ["Your in-game blacklist, as last read from the game."] = "마지막으로 게임에서 읽어온 차단 목록입니다.",
        ["Clears the persisted blacklist cache, then re-reads from the game."] = "저장된 차단 목록 캐시를 비운 뒤 게임에서 다시 읽습니다.",
        ["Clear cached names?"] = "저장된 이름을 삭제할까요?",
        ["This permanently deletes all {0} stored names and their history. This cannot be undone."]
            = "저장된 이름 {0}개와 이름 기록을 영구히 삭제합니다. 되돌릴 수 없습니다.",

        // ── Command help (shown in the Dalamud plugin installer / help list) ──
        ["Open the settings window."] = "설정 창을 엽니다.",
        ["Toggle the Party List Overlay on or off."] = "파티 목록 오버레이를 켜거나 끕니다.",

        // ── PF member info overlay ───────────────────────────────────────────
        ["Member Info"] = "멤버 정보",
        ["No party finder listing selected."] = "선택된 파티 찾기 모집이 없습니다.",
        ["Open a PF detail window to see member info."] = "파티 찾기 상세 창을 열면 멤버 정보가 표시됩니다.",
        ["Not a high-end duty."] = "고난도 임무가 아닙니다.",
        ["Tomestone Lookup"] = "Tomestone 조회",
        ["Tomestone API Key Needed"] = "Tomestone API 키 필요",
        ["Configure your Tomestone API key in Settings → Tomestone."] = "설정 → Tomestone에서 Tomestone API 키를 설정하세요.",
        ["FFLogs Lookup"] = "FFLogs 조회",
        ["FFLogs API Key Needed"] = "FFLogs API 키 필요",
        ["Configure your FFLogs credentials in Settings → FFLogs."] = "설정 → FFLogs에서 FFLogs 자격 증명을 설정하세요.",
        ["Waiting for player names to be resolved…"] = "플레이어 이름 확인 중…",
        ["Looking up Tomestone data for all players…"] = "모든 플레이어의 Tomestone 데이터 조회 중…",
        ["Look up Tomestone data for all players"] = "모든 플레이어의 Tomestone 데이터 조회",
        ["Tomestone data already loaded for this listing"] = "이 모집의 Tomestone 데이터를 이미 불러왔습니다",
        ["Looking up FFLogs data for all players…"] = "모든 플레이어의 FFLogs 데이터 조회 중…",
        ["Look up FFLogs data for all players"] = "모든 플레이어의 FFLogs 데이터 조회",
        ["FFLogs data already loaded for this listing"] = "이 모집의 FFLogs 데이터를 이미 불러왔습니다",
        ["Fetching more data…"] = "데이터 더 가져오는 중…",
        ["Player"] = "플레이어",
        ["Name"] = "이름",
        ["Private"] = "비공개",
        ["Adventure plate is hidden or unavailable"] = "모험가 카드가 숨겨져 있거나 사용할 수 없습니다",
        ["On your blacklist"] = "내 차단 목록에 있음",
        ["Click to open FFLogs page"] = "클릭하여 FFLogs 페이지 열기",
        ["Data age"] = "데이터 나이",
        ["This name is old and may be out of date."] = "이 이름은 오래되어 현재와 다를 수 있습니다.",
        ["Previously seen as:"] = "이전 이름:",
        // Relative time ({0}=number) and previous-name date prefix.
        ["just now"] = "방금 전",
        ["{0}m ago"] = "{0}분 전",
        ["{0}h ago"] = "{0}시간 전",
        ["{0}d ago"] = "{0}일 전",
        ["{0}y ago"] = "{0}년 전",
        ["until {0}"] = "{0}까지",

        // ── Overlay cells (shared by both overlays) ──────────────────────────
        ["Lookup failed"] = "조회 실패",
        ["Not on FFLogs"] = "FFLogs 미등록",
        ["FFLogs has no character with this name on this server. They may never have uploaded a log, or renamed or changed servers recently."]
            = "FFLogs에 이 서버의 이 이름으로 된 캐릭터가 없습니다. 로그를 올린 적이 없거나, 최근 닉네임을 변경했거나 서버를 이전했을 수 있습니다.",
        ["FFLogs has no character with this name on this server"] = "FFLogs에 이 이름의 캐릭터가 없음",
        ["FFLogs lookup failed (network or rate limit) — refresh to retry."] = "FFLogs 조회 실패 (네트워크 또는 요청 한도) — 새로고침하여 재시도.",
        ["No Logs"] = "기록 없음",
        ["No logs"] = "기록 없음",
        ["Logs hidden"] = "로그 비공개",
        ["The character's FFLogs rankings are private"] = "FFLogs 랭킹이 비공개로 설정됨",
        ["This character has made their FFLogs rankings private, so clears can't be shown."] = "이 캐릭터는 FFLogs 랭킹을 비공개로 설정해 클리어 여부를 표시할 수 없습니다.",
        ["FFLogs doesn't record progression for this duty, so only clears are shown."] = "이 콘텐츠는 FFLogs가 진도를 기록하지 않아 클리어 여부만 표시합니다.",
        // FFLogs result formats ({0}=kills, {1}/{2}=parse%) — templates so word order localizes.
        ["Cleared {0}X"] = "{0}킬",
        ["{0}% wipe"] = "{0}% 전멸",
        ["Average overall parse {0}%"] = "최근 영식 평균 {0}%",
        ["Hidden Profile"] = "비공개 프로필",
        ["N/A"] = "해당 없음",

        // ── Party list overlay window ────────────────────────────────────────
        ["Party Members"] = "파티원",
        ["Hide the party list overlay. Turn it back on in settings or with /pcrparty."]
            = "파티 목록 오버레이를 숨깁니다. 설정이나 /pcrparty 명령어로 다시 켤 수 있습니다.",
        ["Waiting for party data…"] = "파티 데이터 대기 중…",
        ["Duty"] = "임무",
        ["(None)"] = "(없음)",
        ["Loading FFLogs & Tomestone data…"] = "FFLogs & Tomestone 데이터 불러오는 중…",
        ["Loading FFLogs data…"] = "FFLogs 데이터 불러오는 중…",
        ["Loading Tomestone data…"] = "Tomestone 데이터 불러오는 중…",

        // ── Chat ─────────────────────────────────────────────────────────────
        ["Party List Overlay shown"] = "파티 목록 오버레이 표시됨",
        ["Party List Overlay hidden"] = "파티 목록 오버레이 숨김",
    };
}
