# Achicrack architecture

Achicrack is a Windows background application that observes **local, read-only** achievement state of
games that use a Steamworks-compatible implementation, and celebrates new unlocks with a PS5-inspired
overlay. It never modifies game files, never injects code and never needs a Steam login.

```
┌──────────────────────────── Achicrack.App (WPF, net10.0-windows) ────────────────────────────┐
│  Views (XAML) ◄─► ViewModels (MVVM, CommunityToolkit.Mvvm)        System tray (NotifyIcon)    │
│  OverlayWindow + NotificationCard  ◄── WpfNotificationPresenter   JSON-lines file logger      │
│  WindowsProcessProvider (Process.GetProcesses + QueryFullProcessImageName, foreground window)│
└──────────────────────────────────────────────┬───────────────────────────────────────────────┘
                                               │ HostPlatform (presenter, process provider, logger)
┌──────────────────────────── Achicrack.Core (net10.0, UI-agnostic) ───────────────────────────┐
│                                                                                              │
│  Game detection          Game identification        Catalog resolution     State monitoring │
│  GameDetectionService ─► GameProcessMatcher          AchievementCatalog-    IGameAdapter     │
│   (poll, grace period,    (full path / install dir,   Service + IconCache    ├ GoldbergAdapter│
│    foreground primary)     launcher exclusion)        (+ optional rarity)    └ SimulatedAdapt.│
│        │                                                     ▲               FileStateMonitor │
│        ▼                                                     │                   │            │
│  AutoTrackingService ──► TrackingCoordinator ────────────────┘                   │ snapshots  │
│                          (GameDetected → Initializing → Active/Unavailable → Stopped)         │
│                                   │                                              ▼            │
│                                   └──────────────► AchievementEventEngine ◄──────┘            │
│                                                    (validate, dedupe, persist, publish)       │
│                                                          │                                    │
│                       SQLite (games, unlocks, catalogs)  ◄┘     NotificationRouter ─► Queue ─►│ presenter
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## Projects

| Project | Target | Purpose |
|---|---|---|
| `src/Achicrack.Core` | `net10.0` | All logic: persistence, adapters, monitoring, detection, tracking, notification queue. No UI or Win32 dependencies → fully unit-testable on any OS. |
| `src/Achicrack.App` | `net10.0-windows` | WPF UI, overlay, tray, Windows process provider, file logger, composition (`App.xaml.cs`). |
| `tools/Achicrack.FakeGame` | `net10.0` | Test fixture: builds a fake Goldberg/GSE game folder, writes `achievements.json` exactly like the emulator, can run as a long-lived "game" process. Deliberately independent of Core. |
| `tests/Achicrack.Core.Tests` | `net10.0` | xUnit tests (parsers, engine, persistence, monitor, catalog, scanner, detection, end-to-end pipeline). |

## Key flows

### Live unlock
1. `FileStateMonitor` gets a `FileSystemWatcher` event (or a periodic reconciliation finds a changed
   length/mtime), debounces, reads the file with `FileShare.ReadWrite|Delete`, retries on sharing
   violations and parse failures (torn writes) and never replaces the last good snapshot with a bad one.
2. The snapshot goes to `AchievementEventEngine.ProcessSnapshot(..., SnapshotKind.Live)`.
3. The engine checks its per-game set of known unlocks (loaded from SQLite), inserts new rows with
   `INSERT OR IGNORE` (so even two engine instances cannot double-insert) and raises `AchievementUnlocked`.
4. `NotificationRouter` builds a `NotificationRequest` from catalog metadata (name, description, icon,
   rarity) — falling back to the API name, never inventing text — and enqueues it.
5. `NotificationQueue` presents one popup at a time (dedup window 30 s), the WPF presenter shows the overlay.
6. View models receive the same event and flip the item from locked to unlocked in place.

### Baseline vs. live
The first read when monitoring starts is a **baseline**: unlocks found there are persisted with
`is_baseline = 1` and never notified. This covers achievements earned before Achicrack existed, while
it was closed, or between sessions. Duplicate notifications are impossible because the database
(primary key `(game_id, api_name)`) is the source of truth across restarts.

### Game lifecycle (M11)
`GameDetectionService` polls the process list every 2 s (cheap: no handles), resolves executable paths
only for *new* processes and matches them by full path or install directory (launchers, crash handlers
and installers are excluded; ambiguous matches are reported, not guessed). Multiple processes of one game
form one running game; an exit grace period (5 s) absorbs launcher → game handoffs, so relaunches do not
produce duplicate "Started Tracking" popups. The foreground window decides the primary "Currently Playing"
game; switching windows never stops background monitoring.

`TrackingCoordinator` drives `GameDetected → InitializingTracker → TrackingActive | TrackingUnavailable →
TrackingStopped`. "Started Tracking" is only raised after the adapter's monitor has read its baseline.
On exit it performs a final reconcile so unlocks written right before the game closed are not lost.

## Data locations
* `%LOCALAPPDATA%\Achicrack\achicrack.db` — SQLite (WAL). Override in Settings.
* `%LOCALAPPDATA%\Achicrack\settings.json` — settings and notification presets (atomic writes; a corrupt
  file is backed up and replaced by defaults).
* `%LOCALAPPDATA%\Achicrack\cache\icons\<gameId>\` — copies of achievement icons.
* `%LOCALAPPDATA%\Achicrack\logs\achicrack-YYYYMMDD.jsonl` — structured diagnostic log (14 days kept).

## Dependencies (NuGet)
| Package | Why |
|---|---|
| `Microsoft.Data.Sqlite` | SQLite persistence (bundles native SQLite). |
| `Microsoft.Extensions.Logging(.Abstractions)` | `ILogger` in Core, `LoggerFactory` in the app. |
| `CommunityToolkit.Mvvm` | `ObservableProperty` / `RelayCommand` source generators for MVVM. |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | Tests only. |

WinForms (part of the Windows Desktop SDK, not a NuGet package) is referenced only for the tray icon.

## Extending
* **New Steam API implementation** → implement `IGameAdapter` (probe, state paths, monitor, catalog) and
  register it in `AchicrackHost`. Output must be `AchievementStateSnapshot`s; the engine does the rest.
* **Non-file sources** (future API observation) → call `AchievementEventEngine.Accept` with an
  `AchievementObservation`; set `IsCommitted = false` for unconfirmed `SetAchievement` calls (ignored).
* **New notification theme** → the overlay only consumes `NotificationRequest` + `NotificationStyle`.
