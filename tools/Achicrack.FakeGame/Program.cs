using System.Globalization;
using Achicrack.FakeGame;

// Achicrack test fixture: a fake "game" that behaves like a Goldberg/GSE-emulated title.
// It never touches real games; it only writes into the folders you give it.
//
//   fakegame create <gameDir> [--appid 4000001] [--count 8] [--classic] [--saves <root>]
//   fakegame unlock <gameDir> <API_NAME|--next> [--appid ...] [--saves <root>] [--in-place]
//   fakegame run    <gameDir> [--every <seconds>] [--appid ...] [--saves <root>]
//
// Default saves root is %APPDATA%\GSE Saves (or "Goldberg SteamEmu Saves" with --classic),
// i.e. exactly where the real emulator writes. `run` keeps a process alive (for process
// detection tests) and unlocks the next achievement every N seconds (0 = on Enter key).

if (args.Length < 2)
{
    Console.WriteLine("usage: fakegame <create|unlock|run> <gameDir> [options]");
    return 2;
}

var command = args[0].ToLowerInvariant();
var gameDir = args[1];
var appId = uint.Parse(Option("--appid") ?? "4000001", CultureInfo.InvariantCulture);
var classic = args.Contains("--classic");
var savesRoot = Option("--saves") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    classic ? "Goldberg SteamEmu Saves" : "GSE Saves");
var fixture = new GoldbergFixture(gameDir, appId, savesRoot);

switch (command)
{
    case "create":
        fixture.Create(int.Parse(Option("--count") ?? "8", CultureInfo.InvariantCulture), gse: !classic);
        CopySelfInto(fixture.BinDirectory);
        Console.WriteLine($"Created fake game at {fixture.GameDirectory}");
        Console.WriteLine($"State file will be: {fixture.StateFilePath}");
        Console.WriteLine($"Register the folder in Achicrack, then run: fakegame run \"{gameDir}\"");
        return 0;

    case "unlock":
    {
        var target = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : NextLocked();
        if (target is null)
        {
            Console.WriteLine("All achievements are already unlocked.");
            return 1;
        }

        if (args.Contains("--in-place"))
        {
            fixture.UnlockInPlace(target);
        }
        else
        {
            fixture.Unlock(target);
        }

        Console.WriteLine($"Unlocked {target} → {fixture.StateFilePath}");
        return 0;
    }

    case "run":
    {
        var every = double.Parse(Option("--every") ?? "0", CultureInfo.InvariantCulture);
        Console.WriteLine($"Fake game running (pid {Environment.ProcessId}). " +
                          (every > 0 ? $"Unlocking every {every}s." : "Press Enter to unlock the next achievement, Q+Enter to quit."));
        while (true)
        {
            if (every > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds(every));
            }
            else
            {
                var line = Console.ReadLine();
                if (line is null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase))
                {
                    return 0;
                }
            }

            var next = NextLocked();
            if (next is null)
            {
                Console.WriteLine("Everything unlocked. Still running; press Ctrl+C to exit.");
                if (every > 0)
                {
                    Thread.Sleep(Timeout.Infinite);
                }

                continue;
            }

            fixture.Unlock(next);
            Console.WriteLine($"[{DateTime.Now:T}] Unlocked {next}");
        }
    }

    default:
        Console.WriteLine($"Unknown command '{command}'.");
        return 2;
}

string? Option(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

string? NextLocked()
{
    var manifest = Path.Combine(fixture.SteamSettingsDirectory, "achievements.json");
    var names = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!.AsArray()
        .Select(n => n!["name"]!.GetValue<string>()).ToList();
    var state = File.Exists(fixture.StateFilePath)
        ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.StateFilePath))!.AsObject()
        : new System.Text.Json.Nodes.JsonObject();
    return names.FirstOrDefault(n => state[n]?["earned"]?.GetValue<bool>() != true);
}

static void CopySelfInto(string binDir)
{
    // Copies this tool next to the fake steam_api64.dll so `FakeGame.exe` inside the game folder is a
    // real, launchable process that Achicrack's process detection can match by path.
    var self = AppContext.BaseDirectory;
    foreach (var file in Directory.EnumerateFiles(self, "Achicrack.FakeGame*"))
    {
        File.Copy(file, Path.Combine(binDir, Path.GetFileName(file)), overwrite: true);
    }

    var apphost = Path.Combine(self, OperatingSystem.IsWindows() ? "Achicrack.FakeGame.exe" : "Achicrack.FakeGame");
    if (File.Exists(apphost) && OperatingSystem.IsWindows())
    {
        File.Copy(apphost, Path.Combine(binDir, "FakeGame.exe"), overwrite: true);
    }
}
