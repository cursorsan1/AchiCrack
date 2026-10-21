# Research: observing Steamworks achievement API calls (Method B)

Status: **not implemented in this release, by design.**

## What would be observed
| Call | Meaning | Why it is not enough on its own |
|---|---|---|
| `ISteamUserStats::SetAchievement(name)` | Marks an achievement as achieved *in memory*. | Not yet persisted. The game may never call `StoreStats`, or the store may fail. |
| `ISteamUserStats::StoreStats()` | Requests that pending stats/achievements be committed. | Asynchronous: success is reported later via the `UserStatsStored_t` callback (`m_eResult`). With the real client the commit happens server-side. |
| `ISteamUserStats::GetAchievement(name, &achieved)` | Reads the current state. | Read-only; useful for reconciliation, not as an event. |
| `ISteamUserStats::GetAchievementDisplayAttribute(name, "name"/"desc"/"hidden")` | Localized metadata. | Metadata only. |

A trustworthy "unlocked" event is therefore `SetAchievement` **followed by a successful
`UserStatsStored_t`**, or — for emulators — the moment the emulator writes its local save file.
Achicrack's engine models this with `AchievementObservation.IsCommitted`: uncommitted observations are
ignored and never notified.

## Why it requires invasive instrumentation
The calls happen inside the game process through `steam_api(64).dll`. Observing them requires one of:

1. **Proxy DLL** (replacing or chaining `steam_api64.dll`) — modifies the game folder; conflicts with
   integrity checks, DRM and anti-cheat.
2. **In-process hooking / DLL injection** (Detours, MinHook) — modifies executable memory of the game;
   flagged by anti-cheat, can crash games, and is exactly what the project rules forbid.
3. **Debugger-based breakpoints** — far too intrusive for gameplay and blocked by many games.

None of these is acceptable for the initial release (no DLL replacement, no injection, no memory
modification, no DRM/anti-cheat interaction).

## What Achicrack does instead
* Emulators that persist achievements locally (Goldberg, gbe_fork/GSE) are observed through their state
  file — the file write *is* the commit, which is exactly the semantic we want.
* The adapter interface (`IGameAdapter`) and `AchievementEventEngine.Accept(observation)` already accept
  events from any future source, so an opt-in, separately distributed integration could feed verified,
  committed events without changes to the engine, database, UI or overlay.

## Possible future, non-invasive avenues (unverified)
* Emulator-provided hooks or IPC (some forks expose overlay callbacks) — would need the emulator's
  cooperation, not injection.
* Reading Steam client local caches for games run through the real client — the client's local formats
  are undocumented and server-authoritative; would be read-only but is out of scope for now.
