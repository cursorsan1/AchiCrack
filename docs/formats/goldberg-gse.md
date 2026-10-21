# Goldberg Steam Emulator / gbe_fork (GSE) — formats used by Achicrack

Implemented in `src/Achicrack.Core/Adapters/Goldberg`. Everything is read-only.

## Identification (scanner)
Next to `steam_api.dll` / `steam_api64.dll`:

| Marker | Meaning |
|---|---|
| `steam_settings\` folder | Goldberg-family emulator configuration. |
| `steam_settings\configs.*.ini` | gbe_fork / GSE (classic Goldberg used `*.txt` flag files). |
| `steam_interfaces.txt` | Goldberg-family interface list. |
| `steam_appid.txt` (DLL folder or `steam_settings\`) | Steam App ID. |
| `local_save.txt` | Classic Goldberg portable save mode. |

A steam_api DLL **without** these markers is reported as *Unverified* — it is most likely the genuine
Steam API, which cannot be monitored offline. A game is never declared supported only because a
steam_api DLL exists.

## State file
`%APPDATA%\GSE Saves\<appid>\achievements.json` (gbe_fork) or
`%APPDATA%\Goldberg SteamEmu Saves\<appid>\achievements.json` (classic). Achicrack watches all candidates
and merges them (an achievement is earned if any file says so).

```json
{
  "ACH_WIN_ONE_GAME": { "earned": true,  "earned_time": 1617203350 },
  "ACH_WIN_100_GAMES": { "earned": false, "earned_time": 0 }
}
```

* Keys are Steamworks API names. `earned_time` is a Unix timestamp in seconds.
* Forks may add fields (`progress`, `max_progress`); they are ignored.
* The file may list only earned achievements → absence never means "known locked".
* The emulator rewrites the whole file; reads can observe a truncated file. Such reads fail to parse and
  are retried; the last good snapshot is kept.

Portable layouts (implemented from documentation, reported as **Unverified** until validated with a real game):
* gbe_fork `steam_settings\configs.user.ini` → `[user::saves] local_save_path=` (relative to the DLL
  folder) and `saves_folder_name=`.
* classic `local_save.txt` next to the DLL naming a folder relative to it.

## Achievement manifest (catalog)
`steam_settings\achievements.json` — an array generated from the game's Steam schema:

```json
[
  {
    "name": "ACH_WIN_ONE_GAME",
    "displayName": "Winner"            // or { "english": "Winner", "german": "Gewinner", ... }
    "description": "Win one game.",     // same: string or localized object
    "hidden": "0",                      // "0"/"1", 0/1 or bool
    "icon": "images/abc.jpg",           // relative to steam_settings (also tried: images/, achievement_images/)
    "icon_gray": "images/abc_gray.jpg"  // "icongray" also accepted
  }
]
```

Because it is generated from the full schema, a non-empty manifest is treated as a **complete catalog**.
Without it, Achicrack lists only unlocks it has seen (by API name) and marks the list as incomplete.

## Verification status
* Parsers, monitor and adapter are covered by automated tests against fixtures produced by
  `tools/Achicrack.FakeGame`, which writes files the same way the emulator does.
* **Not yet validated against a real game running the real emulator.** See `docs/VALIDATION.md`.
