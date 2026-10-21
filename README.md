# Achicrack

Local Steam achievement tracker for Windows with PlayStation 5-inspired trophy notifications.

Achicrack runs in the background, recognizes registered games when they start, reads the
achievement state that Steamworks-compatible implementations (currently **Goldberg Steam Emulator** and
**gbe_fork / GSE**) store on disk, and celebrates new unlocks with an animated overlay. It works offline,
needs no Steam account, and is strictly **read-only**: no DLL replacement, no code injection, no memory
modification, no DRM or anti-cheat interaction.

## Features
* Automatic game recognition (process path matching, launcher exclusion, multi-process games, foreground
  "currently playing" selection) and tracking lifecycle: *Game Detected → Initializing Tracker →
  Tracking Active / Unavailable → Tracking Stopped*.
* "Started Tracking" and "Achievement Unlocked" overlay popups: dark translucent card, rounded corners,
  icon, trophy glyph colored by rarity, slide/drop/fade/pop animations, queueing, click-through,
  no focus stealing, optional sound.
* Complete achievement library per game (locked + unlocked) from the emulator's achievement manifest,
  with icons, unlock dates, API names and optional global rarity. Incomplete catalogs are labeled as such;
  names are never invented (unknown → API identifier + default trophy).
* Dashboard, Currently Playing, Game Library (read-only scan with Supported / Unverified / Unsupported
  report), Achievements, Notification Studio (live preview, presets, sound, position, scale, opacity,
  font, colors), Settings, system tray.
* SQLite history with duplicate-proof unlock records; safe across crashes and restarts.

## Build & run
Requires the .NET 10 SDK. The app targets Windows; Core and tests are cross-platform.

```powershell
dotnet build Achicrack.slnx
dotnet test                                   # automated tests (simulated fixtures)
dotnet run --project src/Achicrack.App        # start the app (Windows)
dotnet run --project src/Achicrack.App -- --test-notification   # show a test popup at startup
dotnet publish src/Achicrack.App -c Release -r win-x64 --self-contained -o publish
```

Command-line switches: `--minimized` (start in tray, used by autostart), `--test-notification`,
`--simulate-unlock`.

### Test fixture (no real game needed)
```powershell
dotnet run --project tools/Achicrack.FakeGame -- create C:\Temp\FakeGame --appid 4000001
C:\Temp\FakeGame\bin\FakeGame.exe run C:\Temp\FakeGame      # Enter = unlock next achievement
```

## Documentation
* [Architecture](docs/ARCHITECTURE.md)
* [Goldberg / GSE formats](docs/formats/goldberg-gse.md)
* [Validation status and Windows test plan](docs/VALIDATION.md)
* [Research: observing Steamworks API calls](docs/research/steamworks-api-observation.md)

## Status
Milestones M1–M8 and M11 are implemented and covered by automated tests at the Core level. The WPF layer
compiles but has not yet been run on Windows, and no real-game validation has been performed yet — see
[VALIDATION.md](docs/VALIDATION.md). Additional adapters (M9) and production validation/packaging (M10)
are open.
