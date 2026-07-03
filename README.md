# [![](https://raw.githubusercontent.com/FFXIV-CombatReborn/RebornAssets/main/IconAssets/PCR_Icon.png)](https://github.com/pnsdh/PassportCheckerReborn)

**Passport Checker Reborn (Custom)**

파이널 판타지 14용 파티 찾기 강화 플러그인입니다. [Dalamud](https://github.com/goatcorp/Dalamud) 플러그인 프레임워크 기반이며, 파티 찾기 목록 옆에 멤버 정보 오버레이를 표시하고 [FFLogs](https://www.fflogs.com/)와 연동해 플레이어의 로그·진행도를 빠르게 확인합니다.

> **참고:** 이 저장소는 **한국 서버 전용으로 수정한 포크 빌드**입니다(한국어 월드명 → FFLogs 매핑, 한국어 UI, PlayerTrack 기반 비공개 플레이어 이름 복원). 원본 프로젝트 [FFXIV-CombatReborn/PassportCheckerReborn](https://github.com/FFXIV-CombatReborn/PassportCheckerReborn)와는 별개입니다.

## 주요 기능

### 파티 찾기 오버레이
- **멤버 정보 오버레이** — 파티 찾기 상세 창 옆에 자동으로 열려 멤버의 이름·직업·아이콘을 표시합니다.
- **FFLogs 연동** — 직업별 로그 백분위를 로그 등급 색상(회색 → 초록 → 파랑 → 보라 → 주황)으로 표시하고, 킬이 없으면 진행도(마지막 페이즈 + 보스 HP%), 절의 확장팩 통합 클리어 집계까지 보여줍니다.
- **PlayerTrack 이름 복원** — 모험가 카드를 숨긴(`[비공개]`) 플레이어의 이름을 [PlayerTrack](https://github.com/Infiziert90/PlayerTrack) 플러그인의 로컬 DB(읽기 전용)에서 복원합니다. 복원된 이름은 캐시되며 `[PT]` 태그로 표시됩니다.


### 파티 목록 오버레이
- **현재 파티 정보** — 게임 내 파티원 목록에 붙는 별도 오버레이로, 파티원의 FFLogs 데이터를 표시합니다.
- **위치 설정** — 왼쪽 / 오른쪽 / 위 / 아래 또는 자유 배치.
- **콘텐츠 선택** — 파티 단위 조회 시 특정 전투를 지정할 수 있습니다.
- **자동 숨김** — 인스턴스 진행 중이나 전투 중에는 오버레이를 숨길 수 있습니다.
- **크로스월드 지원** — `InfoProxyCrossRealm`로 크로스월드 파티를 감지합니다.

### 파티 찾기 목록 기능
- **자동 새로고침** — 지정한 간격(10~120초)마다 파티 찾기 목록을 새로고침하며, 상세 창이 열려 있는 동안엔 멈춥니다.

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

### 일반 (General) 탭
파티 찾기 상세/목록 관련 기능을 설정합니다(직업 아이콘, 파티 변동 시 창 유지, 자동 새로고침).

### 오버레이 (Overlay) 탭
멤버 정보 오버레이, 고난도 콘텐츠 필터, 오버레이 방향, FFLogs 연동을 켜고 끕니다. 파티 목록 오버레이의 위치·자동 숨김, 이름 재확인 설정도 여기에 있습니다.

### FFLogs 연동 탭
FFLogs API의 Client ID와 Client Secret을 입력하고 **Save & Test Credentials**로 인증을 확인합니다.

<details>
<summary>FFLogs API 자격 증명 발급 방법</summary>

1. [FFLogs API 포털](https://www.fflogs.com/api/clients/)에 접속합니다.
2. **Create Client**를 클릭합니다.
3. 클라이언트 이름을 입력합니다(예: `PassportCheckerReborn`).
4. Redirect URL은 아무 값이나 넣습니다(예: `https://example.com/`).
5. **Public Client**는 체크하지 않습니다.
6. 발급된 Client ID와 Client Secret을 플러그인 설정에 입력합니다.
</details>

### PlayerTrack 탭
[PlayerTrack](https://github.com/Infiziert90/PlayerTrack) DB를 읽어 모험가 카드를 숨긴 플레이어의 이름을 복원하도록 설정합니다. 실시간 모험가 카드와 PlayerTrack 중 무엇을 먼저 조회할지 선택할 수 있고, PlayerTrack의 설치/실행 상태도 표시됩니다.

## 소스에서 빌드

[Dalamud .NET SDK](https://github.com/goatcorp/Dalamud) v15와 .NET 10이 필요합니다.

```bash
dotnet restore
dotnet build
```

## 라이선스

이 프로젝트는 [GNU Affero General Public License v3.0](LICENSE.md)을 따릅니다. 원본 프로젝트: [FFXIV-CombatReborn/PassportCheckerReborn](https://github.com/FFXIV-CombatReborn/PassportCheckerReborn).
