# [![](https://raw.githubusercontent.com/FFXIV-CombatReborn/RebornAssets/main/IconAssets/PCR_Icon.png)](https://github.com/pnsdh/PassportCheckerReborn)

**Passport Checker Reborn (Custom)**

파이널 판타지 14용 파티 찾기 강화 플러그인입니다. [Dalamud](https://github.com/goatcorp/Dalamud) 플러그인 프레임워크 기반이며, 파티 찾기 목록 옆에 멤버 정보 오버레이를 표시하고 [FFLogs](https://www.fflogs.com/)와 연동해 플레이어의 로그·진행도를 빠르게 확인합니다.

> **참고:** 이 저장소는 **한국 서버 전용으로 수정한 포크 빌드**입니다(한국어 월드명 → FFLogs 매핑, 한국어 UI, PlayerTrack 기반 비공개 플레이어 이름 복원). 원본 프로젝트 [FFXIV-CombatReborn/PassportCheckerReborn](https://github.com/FFXIV-CombatReborn/PassportCheckerReborn)와는 별개입니다.

## 주요 기능

### 파티 찾기 오버레이
- **멤버 정보 오버레이** — 파티 찾기 상세 창 옆에 자동으로 열려 멤버의 이름·직업·아이콘을 표시합니다.
- **FFLogs 연동** — 직업별 로그 백분위를 로그 등급 색상(회색 → 초록 → 파랑 → 보라 → 주황)으로 표시하고, 킬이 없으면 진행도(마지막 페이즈 + 보스 HP%), 절의 확장팩 통합 클리어 집계까지 보여줍니다.
- **아는 플레이어 강조** — 친구, 자유부대원, 링크셸·크로스월드 링크셸 멤버를 줄 색으로 강조합니다(항목별로 켜고 끌 수 있음). 게임이 각 명단을 해당 창을 열 때 불러오므로, 한 번씩 열어 두면 캐릭터별로 기억합니다.
- **FFLogs 미등록 표시** — FFLogs에 해당 캐릭터가 없으면(로그를 올린 적 없음, 닉네임 변경·서버 이전 등) "기록 없음"과 구분해 표시합니다.
- **PlayerTrack 이름 복원** — 모험가 카드를 숨긴(`[비공개]`) 플레이어의 이름을 [PlayerTrack](https://github.com/Infiziert90/PlayerTrack) 플러그인의 로컬 DB(읽기 전용)에서 복원합니다. 복원된 이름은 캐시되며 `[PT]` 태그로 표시됩니다.


### 파티 목록 오버레이
- **현재 파티 정보** — 게임 내 파티원 목록에 붙는 별도 오버레이로, 파티원의 FFLogs 데이터를 표시합니다.
- **위치 설정** — 왼쪽 / 오른쪽 / 위 / 아래 또는 자유 배치.
- **콘텐츠 선택** — 파티 단위 조회 시 특정 전투를 지정할 수 있습니다.
- **자동 숨김** — 인스턴스 진행 중이나 전투 중에는 오버레이를 숨길 수 있습니다.
- **크로스월드 지원** — `InfoProxyCrossRealm`로 크로스월드 파티를 감지합니다.

### 파티 찾기 목록 기능
- **자동 새로고침** — 지정한 간격(10~120초)마다 파티 찾기 목록을 새로고침하며, 상세 창이 열려 있는 동안엔 멈춥니다.
- **남은 시간 순 정렬** — 임무별로 묶는 대신 각 페이지의 모집을 남은 시간 순(최신 순 또는 오래된 순)으로 정렬합니다. 파티 찾기의 정렬 버튼으로 순서를 뒤집을 수 있습니다.
- **페이지당 100개** — 서버에 모집을 50개 대신 100개씩 요청합니다.
- **원클릭 직업 필터** — 파티 찾기 위의 버튼 하나로, 현재 직업으로 들어갈 빈자리가 없는 고난도 임무 모집을 숨깁니다.
- **모집 정보 보기** — 플레이어 우클릭 메뉴에서 그 플레이어의 파티 찾기 모집을 바로 엽니다.
- **파티 변동 시 창 유지** — 파티원이 들어오거나 나가도 파티 찾기가 닫히지 않고, 보던 모집을 다시 불러옵니다.

## 명령어

| 명령어 | 설명 |
|---|---|
| `/pfchecker` (또는 `/pcr`) | 설정 창 열기 |
| `/pcrparty` | 파티 목록 오버레이 켜기/끄기 |

## 설치

1. 채팅창에 `/xlsettings`를 입력하고 **Experimental**(실험적) 탭으로 이동합니다.
2. **Custom Plugin Repositories**(사용자 지정 플러그인 저장소) 섹션의 빈 칸에 아래 주소를 붙여넣습니다:
```
https://raw.githubusercontent.com/pnsdh/DalamudPlugins/main/pluginmaster.json
```
3. **+** 버튼을 누르고 옆의 체크 표시가 켜졌는지 확인합니다.
4. 우측 하단의 **저장**(디스크) 아이콘을 클릭합니다.
5. 플러그인 설치 창(`/xlplugins`)에서 **Passport Checker Reborn (Custom)**을 설치합니다.

## 설정

`/pfchecker`(또는 `/pcr`) 또는 Dalamud 플러그인 설치 창에서 설정을 엽니다.

설정 창은 왼쪽 메뉴에서 페이지를 골라 이동합니다.

### 일반
파티 찾기 상세/목록 관련 기능을 설정합니다(직업 아이콘, 파티 변동 시 창 유지, 남은 시간 순 정렬, 페이지당 100개, 원클릭 직업 필터, 자동 새로 고침, 우클릭 모집 정보 보기, 차단 목록).

### 오버레이
멤버 정보 오버레이(고난도 임무 필터, 표시 방향, 이름 표시)와 파티 목록 오버레이(위치, 자동 숨김)를 설정합니다. 이름 재확인(이름 최신화) 설정도 여기에 있습니다.

### FFLogs
FFLogs 연동을 켜고 끄며, FFLogs API의 클라이언트 ID와 클라이언트 시크릿을 입력한 뒤 **저장 및 테스트**로 인증을 확인합니다. 이름이 모두 확인되면 자동 조회하는 옵션과 API 사용량도 여기에 있습니다.

<details>
<summary>FFLogs API 자격 증명 발급 방법</summary>

1. [FFLogs API 포털](https://www.fflogs.com/api/clients/)에 접속합니다.
2. **Create Client**를 클릭합니다.
3. 클라이언트 이름을 입력합니다(예: `PassportCheckerReborn`).
4. Redirect URL은 아무 값이나 넣습니다(예: `https://example.com/`).
5. **Public Client**는 체크하지 않습니다.
6. 발급된 Client ID와 Client Secret을 플러그인 설정에 입력합니다.
</details>

### PlayerTrack
[PlayerTrack](https://github.com/Infiziert90/PlayerTrack) DB를 읽어 모험가 카드를 숨긴 플레이어의 이름을 복원하도록 설정합니다. 실시간 모험가 카드와 PlayerTrack 중 무엇을 먼저 조회할지 선택할 수 있고, PlayerTrack의 설치/실행 상태도 표시됩니다.

### 화면
UI 언어(English/한국어), 플러그인 창의 글자 크기와 강조 색상을 설정합니다.

## 소스에서 빌드

[Dalamud .NET SDK](https://github.com/goatcorp/Dalamud) v15와 .NET 10이 필요합니다.

UI는 [RebornMaterial의 한글 대응 포크](https://github.com/pnsdh/RebornMaterial)를 git 서브모듈(`lib/RebornMaterial`)로 사용합니다.

```bash
git clone --recursive https://github.com/pnsdh/PassportCheckerReborn.git
# 이미 받았다면: git submodule update --init
dotnet restore
dotnet build
```

## 문의

https://discord.gg/R75tq68bA9

## 라이선스

이 프로젝트는 [GNU Affero General Public License v3.0](LICENSE.md)을 따릅니다. 원본 프로젝트: [FFXIV-CombatReborn/PassportCheckerReborn](https://github.com/FFXIV-CombatReborn/PassportCheckerReborn).
