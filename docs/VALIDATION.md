# Validation status and Windows test plan

Achicrack separates **simulated/automated verification** (done on every build) from **real-game
validation** (must be performed on Windows with real software). A successful build is not proof that
achievement tracking works.

## What is verified automatically
`dotnet test` runs ~100 xUnit tests against `Achicrack.Core` using the `Achicrack.FakeGame` fixture
(a fake Goldberg/GSE game folder + state files written the way the emulator writes them) and a simulated
process table. Covered: state/manifest parsing, corrupted/truncated/locked files, locked→unlocked
transitions, baseline import, duplicate suppression (incl. across restarts and concurrent engines),
unlock timestamps, SQLite persistence, catalog loading + icon cache + missing icons, notification queue
ordering/dedup/failure isolation, scanner classification (supported / unverified / unsupported) and
read-only guarantee, process start/exit, duplicate processes, launcher exclusion, ambiguous matches,
foreground "primary" selection, multiple simultaneous games, application restart during an active game,
and the full launch → track → unlock → notify → exit pipeline.

These tests run on Linux in CI-like conditions; the WPF layer (overlay, views, tray) is **compiled but not
executed** there.

## What is NOT yet verified (needs Windows)
| Area | Status |
|---|---|
| WPF app launch, tray icon, all views | Compiled only |
| Overlay rendering, animation smoothness, click-through, no focus stealing | Compiled only |
| Overlay over borderless fullscreen games | Unverified |
| Overlay over exclusive fullscreen games | Expected NOT to work (see below) |
| `WindowsProcessProvider` (QueryFullProcessImageName) | Compiled only |
| Real Goldberg / gbe_fork emulator writing `achievements.json` | Unverified (format implemented from documentation, mirrored by the fixture) |
| Portable save layouts (`local_save.txt`, `configs.user.ini local_save_path`) | Unverified, reported as such in the UI |
| Autostart registry entry | Compiled only |

## Fullscreen limitations (expected behavior)
* **Borderless / windowed fullscreen**: a topmost layered window is composited by DWM above the game, so
  the popup should appear. This is the recommended game setting.
* **Exclusive fullscreen (legacy DirectX exclusive mode)**: the game owns the display output and DWM does
  not compose other windows on top; *no ordinary desktop window can appear*. Windows 10/11 "fullscreen
  optimizations" convert many exclusive-mode games to a flip-model borderless path, in which case the popup
  may appear — this depends on the game and is unverified. Achicrack does not hook the game's swap chain
  (that would require injection), so this limitation is by design. Unlocks are still recorded and listed.

## Manual Windows test plan
Prerequisites: Windows 10/11 x64, .NET 10 SDK (or the self-contained publish).

1. **Build & tests**: `dotnet build Achicrack.slnx` and `dotnet test` → all green.
2. **Launch**: `dotnet run --project src/Achicrack.App` (or `Achicrack.exe`). Expect the main window and a
   tray icon. Right-click tray → menu with Open / Show test notification / Simulate achievement unlock / Exit.
3. **Overlay smoke test**: `Achicrack.exe --test-notification`. Expect a dark card sliding in at the
   top-right, staying ~5 s, fading out. While it is visible: click "through" it on the desktop (click must
   reach the window below), and keep typing in another app (focus must not change).
4. **Simulated pipeline**: Notification Studio → "Simulate achievement unlock" (repeat). Expect a popup per
   unlock, queued one after another; Currently Playing shows *Achicrack Demo* with the item turning from
   locked to unlocked and the completion % increasing.
5. **Fake game, end-to-end** (no real game needed):
   ```
   dotnet run --project tools/Achicrack.FakeGame -- create C:\Temp\FakeGame --appid 4000001
   ```
   Register `C:\Temp\FakeGame` in Game Library (expect *Supported*, App ID 4000001, 8 achievements).
   Start the fake game: `C:\Temp\FakeGame\bin\FakeGame.exe run C:\Temp\FakeGame`.
   Expect within ~2 s: "Started Tracking — FakeGame" popup and Currently Playing → *Tracking Active*, 0 / 8.
   Press Enter in the fake game console → "Achievement Unlocked — Fake Achievement 1" popup and live UI update.
   Close the console → status becomes *Tracking Stopped* after the grace period.
6. **Restart safety**: while the fake game runs, exit Achicrack (tray → Exit), unlock two achievements,
   start Achicrack again → no popups for those two (recorded silently), next unlock pops up normally.
7. **Real game (borderless)** with a Goldberg/gbe_fork setup you are legally entitled to use: register the
   game folder, check the scan report, play until an achievement unlocks. Record: popup shown? correct
   name/icon? latency? Any duplicate? Attach `%LOCALAPPDATA%\Achicrack\logs\*.jsonl`.
8. **Exclusive fullscreen** game: confirm whether the popup appears and report it; unlock must still be
   recorded in the Achievements page.
9. **Performance**: with a game running, Achicrack CPU should stay near 0% idle (Task Manager); a popup
   animation should be smooth.

Please record results per step (pass / fail + notes) — real-game results are tracked separately from the
automated test results.
