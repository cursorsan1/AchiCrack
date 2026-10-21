using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Achicrack.FakeGame;

/// <summary>
/// Builds and mutates a fake game folder that mimics a Goldberg/GSE setup, and writes the emulator's
/// achievements.json state file the way the emulator does. Independent from Achicrack.Core on purpose,
/// so tests exercise Core against an external description of the format.
/// </summary>
public sealed class GoldbergFixture
{
    public GoldbergFixture(string gameDirectory, uint appId, string savesRoot)
    {
        GameDirectory = Path.GetFullPath(gameDirectory);
        AppId = appId;
        SavesRoot = Path.GetFullPath(savesRoot);
    }

    public string GameDirectory { get; }

    public uint AppId { get; }

    /// <summary>Folder that plays the role of %APPDATA%\GSE Saves (or Goldberg SteamEmu Saves).</summary>
    public string SavesRoot { get; }

    public string BinDirectory => Path.Combine(GameDirectory, "bin");

    public string SteamSettingsDirectory => Path.Combine(BinDirectory, "steam_settings");

    public string StateFilePath => Path.Combine(SavesRoot, AppId.ToString(CultureInfo.InvariantCulture), "achievements.json");

    public IReadOnlyList<string> AchievementNames { get; private set; } = [];

    /// <summary>Creates the folder layout. <paramref name="gse"/> adds gbe_fork's configs.*.ini marker.</summary>
    public GoldbergFixture Create(int achievementCount = 8, bool gse = true, bool withIcons = true, string? exeName = "FakeGame.exe")
    {
        Directory.CreateDirectory(SteamSettingsDirectory);
        // Placeholder files: only their names matter to Achicrack's read-only scanner.
        File.WriteAllText(Path.Combine(BinDirectory, "steam_api64.dll"), "placeholder (not a real DLL)");
        File.WriteAllText(Path.Combine(BinDirectory, "steam_appid.txt"), AppId.ToString(CultureInfo.InvariantCulture));
        if (exeName is not null)
        {
            File.WriteAllText(Path.Combine(BinDirectory, exeName), "placeholder executable");
        }

        if (gse)
        {
            File.WriteAllText(Path.Combine(SteamSettingsDirectory, "configs.user.ini"),
                "[user::general]\naccount_name=Tester\n");
        }

        var names = new List<string>();
        var manifest = new JsonArray();
        var imagesDir = Path.Combine(SteamSettingsDirectory, "images");
        Directory.CreateDirectory(imagesDir);
        for (var i = 1; i <= achievementCount; i++)
        {
            var api = $"ACH_FAKE_{i:00}";
            names.Add(api);
            var entry = new JsonObject
            {
                ["name"] = api,
                ["displayName"] = i % 2 == 0
                    ? new JsonObject { ["english"] = $"Fake Achievement {i}", ["german"] = $"Falsche Errungenschaft {i}" }
                    : JsonValue.Create($"Fake Achievement {i}"),
                ["description"] = $"Description of fake achievement {i}.",
                ["hidden"] = i == achievementCount ? "1" : "0",
            };
            if (withIcons)
            {
                var icon = $"{api.ToLowerInvariant()}.png";
                var gray = $"{api.ToLowerInvariant()}_gray.png";
                File.WriteAllBytes(Path.Combine(imagesDir, icon), Png.Solid(64, 64, (byte)(40 * i % 255), 140, 220));
                File.WriteAllBytes(Path.Combine(imagesDir, gray), Png.Solid(64, 64, 90, 90, 90));
                entry["icon"] = $"images/{icon}";
                entry["icon_gray"] = $"images/{gray}";
            }

            manifest.Add(entry);
        }

        File.WriteAllText(Path.Combine(SteamSettingsDirectory, "achievements.json"),
            manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        AchievementNames = names;
        return this;
    }

    /// <summary>Unlocks achievements by rewriting the state file atomically (temp file + replace).</summary>
    public void Unlock(params string[] apiNames) => Unlock(DateTimeOffset.UtcNow, apiNames);

    public void Unlock(DateTimeOffset at, params string[] apiNames)
    {
        var state = ReadState();
        foreach (var api in apiNames)
        {
            state[api] = new JsonObject { ["earned"] = true, ["earned_time"] = at.ToUnixTimeSeconds() };
        }

        WriteAtomic(state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Writes the state in place (truncate + write), like the original emulator does.</summary>
    public void UnlockInPlace(params string[] apiNames)
    {
        var state = ReadState();
        foreach (var api in apiNames)
        {
            state[api] = new JsonObject { ["earned"] = true, ["earned_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        }

        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        File.WriteAllText(StateFilePath, state.ToJsonString());
    }

    /// <summary>Simulates a torn write: leaves the file truncated mid-JSON.</summary>
    public void WriteTruncated()
    {
        var full = ReadState().ToJsonString();
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        File.WriteAllText(StateFilePath, full[..Math.Max(1, full.Length / 2)]);
    }

    public void WriteRaw(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)!);
        File.WriteAllText(StateFilePath, content);
    }

    /// <summary>Adds locked entries (earned=false), as some forks write every achievement.</summary>
    public void WriteLockedEntries(params string[] apiNames)
    {
        var state = ReadState();
        foreach (var api in apiNames)
        {
            state[api] = new JsonObject { ["earned"] = false, ["earned_time"] = 0 };
        }

        WriteAtomic(state.ToJsonString());
    }

    private JsonObject ReadState()
    {
        try
        {
            if (File.Exists(StateFilePath) && JsonNode.Parse(File.ReadAllText(StateFilePath)) is JsonObject obj)
            {
                return obj;
            }
        }
        catch (JsonException)
        {
        }

        return new JsonObject();
    }

    private void WriteAtomic(string content)
    {
        var dir = Path.GetDirectoryName(StateFilePath)!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $"achievements.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        File.Move(temp, StateFilePath, overwrite: true);
    }
}

/// <summary>Tiny PNG encoder for solid-color test icons.</summary>
public static class Png
{
    public static byte[] Solid(int width, int height, byte r, byte g, byte b)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width);
        WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // RGB
        Chunk(ms, "IHDR", ihdr);

        var raw = new byte[height * (width * 3 + 1)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (width * 3 + 1);
            for (var x = 0; x < width; x++)
            {
                var i = row + 1 + x * 3;
                var border = x < 4 || y < 4 || x >= width - 4 || y >= height - 4;
                raw[i] = border ? (byte)255 : r;
                raw[i + 1] = border ? (byte)255 : g;
                raw[i + 2] = border ? (byte)255 : b;
            }
        }

        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw);
        }

        Chunk(ms, "IDAT", compressed.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        var crc = new byte[4];
        WriteBE(crc, 0, Crc32(typeBytes.Concat(data).ToArray()));
        s.Write(crc);
    }

    private static void WriteBE(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
