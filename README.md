# [![](https://raw.githubusercontent.com/FFXIV-CombatReborn/RebornAssets/main/IconAssets/PCR_Icon.png)](https://github.com/FFXIV-CombatReborn/PassportCheckerReborn)

**Passport Checker Reborn**

![Github Latest Releases](https://img.shields.io/github/downloads/FFXIV-CombatReborn/PassportCheckerReborn/latest/total.svg?style=for-the-badge)
![Github License](https://img.shields.io/github/license/FFXIV-CombatReborn/PassportCheckerReborn.svg?label=License&style=for-the-badge)
[![](https://dcbadge.limes.pink/api/server/p54TZMPnC9)](https://discord.gg/p54TZMPnC9)

An open-source Party Finder enhancement plugin for Final Fantasy XIV, built on the [Dalamud](https://github.com/goatcorp/Dalamud) plugin framework. Passport Checker Reborn shows a member-info overlay alongside Party Finder listings and integrates with [FFLogs](https://www.fflogs.com/) and [Tomestone.gg](https://tomestone.gg/) for quick player lookup.

> **Note:** This is a customized fork — **Passport Checker Reborn (Custom)** — adapted for the **Korean server** (Korean world-name → FFLogs slug mapping, Korean/English UI, PlayerTrack-based name recovery). It is not affiliated with the upstream project and is distributed only as a test build. Tomestone.gg has no Korean data, so its features are disabled on the KR client.

## Features

### Party Finder Overlay
- **Member Info Overlay** — automatically opens alongside the PF detail pane showing party members' names, jobs, and icons.
- **FFLogs Integration** — on-demand lookup of per-job parse percentiles with colour-coded results (grey → green → blue → purple → orange), no-kill progression (last phase + boss %), and cross-expansion Ultimate clear aggregation.
- **Tomestone.gg Integration** — on-demand prog-point and clear data for the current duty from the Tomestone API (Global client only).
- **PlayerTrack Name Resolution** — recovers the names of players who hide their adventure plate (shown as `[Private]`) by reading the [PlayerTrack](https://github.com/kalilistic/PlayerTrack) plugin's local database (read-only). Resolved names are cached and marked with a `[PT]` tag.
- **Overlay Positioning** — attach the overlay to the left or right side of the PF detail window.
- **High-End Duty Filter** — optionally limit the overlay to Savage, Ultimate, Extreme, Criterion, Chaotic, and Unreal duties.
- **Party Job Icons** — display in-game job icons next to each member, with a text fallback.

### Party List Overlay
- **Current Party Info** — a separate overlay attached to the in-game Party Members list showing FFLogs and Tomestone data for your party.
- **Configurable Position** — Left, Right, Above, Below, or Unbound (free-floating).
- **Duty Selector** — choose a specific encounter for per-party lookups.
- **Auto-Hide** — optionally hide the party list overlay while in duty or combat.
- **Cross-World Support** — detects cross-world parties via `InfoProxyCrossRealm`.

### Party Finder List Enhancements
- **Auto-Refresh** — periodically refreshes the PF listing at a configurable interval (10–120 seconds), pausing while the detail pane is open.

## Commands

| Command | Description |
|---|---|
| `/pfchecker` (or `/pcr`) | Open the settings window |
| `/pcrparty` | Toggle the party list overlay window |

## Installation

- Enter `/xlsettings` in the chat window and go to the Experimental tab in the opening window.
- **Skip below the DevPlugins section to the Custom Plugin Repositories section.**
- Copy and paste the repo.json link into the first free text input field.
```
https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json
```
- Click on the + button and make sure the checkmark beside the new field is set afterwards.
- **Click on the Save-icon in the bottom right.**

## Configuration

Open settings with `/pfchecker` (or `/pcr`) or via the Dalamud plugin installer.

### General Tab
Configure Party Finder detail and list enhancements (job icons, keeping the PF window open on party changes, auto-refresh).

### Overlay Tab
Toggle the member info overlay, high-end duty filter, overlay side, and FFLogs/Tomestone integrations. Configure the party list overlay position and auto-hide behaviour, plus name-freshness re-verification.

### FFLogs Integration Tab
Enter your FFLogs API Client ID and Client Secret, then click **Save & Test Credentials** to verify.

<details>
<summary>How to obtain FFLogs API credentials</summary>

1. Go to the [FFLogs API portal](https://www.fflogs.com/api/clients/).
2. Click **Create Client**.
3. Enter a client name (e.g. `PassportCheckerReborn`).
4. Provide any Redirect URL (e.g. `https://example.com/`).
5. Leave **Public Client** unchecked.
6. Copy the generated Client ID and Client Secret into the plugin settings.
</details>

### Tomestone Integration Tab
Enter your Tomestone API key (Bearer token) and click **Save**.

<details>
<summary>How to obtain a Tomestone API key</summary>

1. Go to your [Tomestone Account Settings](https://tomestone.gg/profile/account).
2. Scroll to the **API access token** section.
3. Click **Generate access token**.
4. Copy the token into the plugin settings.
</details>

### PlayerTrack Tab
Enable reading the [PlayerTrack](https://github.com/kalilistic/PlayerTrack) database (read-only) to recover names of players who hide their adventure plate, and choose whether to try the live adventure plate or PlayerTrack first. The tab shows PlayerTrack's install/load status.

## Building from Source

Requires the [Dalamud .NET SDK](https://github.com/goatcorp/Dalamud) (v14+) and .NET 10.

```bash
dotnet restore
dotnet build
```

## License

This project is licensed under the [GNU Affero General Public License v3.0](LICENSE.md).
