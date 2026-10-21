using System.Security.Cryptography;
using Achicrack.Core.Adapters;
using Achicrack.Core.Adapters.Goldberg;
using Achicrack.Core.Adapters.Simulated;
using Achicrack.Core.Persistence;
using Achicrack.Core.Scanning;
using Achicrack.FakeGame;

namespace Achicrack.Core.Tests;

public class ScannerTests
{
    private static GameScanner Scanner(TempDir dir) =>
        new(new AdapterRegistry([new GoldbergAdapter(new TestEnvironment(dir.Combine("appdata"))), new SimulatedGameAdapter()]));

    [Fact]
    public void Gse_fixture_is_supported_with_app_id_and_paths()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("Fake Game"), 4000010, dir.Combine("appdata", "GSE Saves")).Create(3, gse: true);

        var report = Scanner(dir).Scan(fx.GameDirectory);

        Assert.Equal("Fake Game", report.SuggestedName);
        Assert.Equal(4000010u, report.AppId);
        Assert.Single(report.SteamApiDlls);
        Assert.NotNull(report.SelectedProbe);
        Assert.Equal(DetectionStatus.Supported, report.SelectedProbe!.Status);
        Assert.Equal("GSE (gbe_fork)", report.SelectedProbe.VariantName);
        Assert.Contains(fx.StateFilePath, report.SelectedProbe.StatePaths);
        Assert.NotNull(report.SelectedProbe.CatalogPath);
        Assert.True(report.CanMonitor);
        Assert.Equal(Path.Combine(fx.BinDirectory, "FakeGame.exe"), report.Executables[0]);
        Assert.Contains(report.Methods, m => m.Status == DetectionStatus.Unsupported && m.MethodName.Contains("SetAchievement"));
    }

    [Fact]
    public void Classic_goldberg_layout_is_supported()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("Classic"), 4000011, dir.Combine("appdata", "Goldberg SteamEmu Saves")).Create(2, gse: false);

        var report = Scanner(dir).Scan(fx.GameDirectory);

        Assert.Equal(DetectionStatus.Supported, report.SelectedProbe!.Status);
        Assert.Equal("Goldberg (classic or fork)", report.SelectedProbe.VariantName);
        Assert.Contains(fx.StateFilePath, report.SelectedProbe.StatePaths);
    }

    [Fact]
    public void Steam_api_without_emulator_markers_is_unverified_not_supported()
    {
        using var dir = new TempDir();
        var game = dir.Combine("RealSteamGame");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "steam_api64.dll"), "x");
        File.WriteAllText(Path.Combine(game, "steam_appid.txt"), "480");
        File.WriteAllText(Path.Combine(game, "Game.exe"), "x");

        var report = Scanner(dir).Scan(game);

        Assert.Equal(DetectionStatus.Unverified, report.SelectedProbe!.Status);
        Assert.Contains("genuine Steam API", report.SelectedProbe.Details);
    }

    [Fact]
    public void Missing_app_id_is_reported()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("NoId"), 4000012, dir.Combine("saves")).Create(1);
        File.Delete(Path.Combine(fx.BinDirectory, "steam_appid.txt"));

        var report = Scanner(dir).Scan(fx.GameDirectory);

        Assert.Equal(DetectionStatus.Unverified, report.SelectedProbe!.Status);
        Assert.Null(report.AppId);
        Assert.False(report.CanMonitor);
    }

    [Fact]
    public void Folder_without_steamworks_is_unsupported()
    {
        using var dir = new TempDir();
        var game = dir.Combine("NoSteam");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, "Game.exe"), "x");

        var report = Scanner(dir).Scan(game);

        Assert.Null(report.SelectedProbe);
        Assert.False(report.CanMonitor);
        Assert.Contains(report.Methods, m => m.MethodName.StartsWith("Steamworks API (steam_api DLL)") && m.Status == DetectionStatus.Unsupported);
    }

    [Fact]
    public void Selecting_an_exe_in_bin_x64_infers_the_install_root()
    {
        using var dir = new TempDir();
        var bin = dir.Combine("Cyber Game", "bin", "x64");
        Directory.CreateDirectory(bin);
        var exe = Path.Combine(bin, "CyberGame.exe");
        File.WriteAllText(exe, "x");
        File.WriteAllText(Path.Combine(bin, "UnityCrashHandler64.exe"), "x");

        var report = Scanner(dir).Scan(exe);

        Assert.Equal(dir.Combine("Cyber Game"), report.InstallDirectory);
        Assert.Equal("Cyber Game", report.SuggestedName);
        Assert.Equal(exe, report.Executables[0]);
        Assert.DoesNotContain(report.Executables, e => e.Contains("CrashHandler"));
    }

    [Fact]
    public void Scanning_never_modifies_the_game_folder()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("Immutable"), 4000013, dir.Combine("appdata", "GSE Saves")).Create(3);
        var before = HashTree(fx.GameDirectory);

        Scanner(dir).Scan(fx.GameDirectory);

        Assert.Equal(before, HashTree(fx.GameDirectory));
    }

    [Fact]
    public void Registration_uses_report_and_rejects_duplicates()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("Reg"), 4000014, dir.Combine("saves")).Create(1);
        var report = Scanner(dir).Scan(fx.GameDirectory);
        var registration = new GameRegistrationService(new GameRepository(TestDb.Create(dir)));

        var game = registration.Register(report, nameOverride: "My Game");

        Assert.Equal("My Game", game.Name);
        Assert.Equal(GoldbergAdapter.AdapterId, game.AdapterId);
        Assert.Equal(fx.BinDirectory, game.SteamApiDirectory);
        Assert.Single(game.Executables);
        Assert.Throws<InvalidOperationException>(() => registration.Register(report));
    }

    [Theory]
    [InlineData("480", 480u)]
    [InlineData(" 1245620 \r\n", 1245620u)]
    [InlineData("730 // comment", 730u)]
    [InlineData("abc", null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    public void App_id_parsing(string text, uint? expected) => Assert.Equal(expected, SteamAppIdResolver.ParseAppId(text));

    private static string HashTree(string root) => string.Join("|", Directory
        .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p)
            ? $"{p}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))}:{File.GetLastWriteTimeUtc(p).Ticks}"
            : p));
}

public class GoldbergAdapterPathTests
{
    [Fact]
    public void Gse_configs_user_ini_local_save_path_is_honored_and_marked_unverified()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("Portable"), 4000020, dir.Combine("unused")).Create(1);
        File.WriteAllText(Path.Combine(fx.SteamSettingsDirectory, "configs.user.ini"),
            "[user::saves]\nlocal_save_path=./my_saves\nsaves_folder_name=Custom Saves\n");
        var adapter = new GoldbergAdapter(new TestEnvironment(dir.Combine("appdata")));

        var probe = adapter.Probe(fx.BinDirectory)!;

        Assert.Equal(DetectionStatus.Unverified, probe.Status);
        Assert.Contains(Path.Combine(fx.BinDirectory, "my_saves", "4000020", "achievements.json"), probe.StatePaths);
        Assert.Contains(Path.Combine(dir.Combine("appdata"), "Custom Saves", "4000020", "achievements.json"), probe.StatePaths);
        Assert.Contains(Path.Combine(dir.Combine("appdata"), "GSE Saves", "4000020", "achievements.json"), probe.StatePaths);
    }

    [Fact]
    public void Classic_local_save_txt_is_honored()
    {
        using var dir = new TempDir();
        var fx = new GoldbergFixture(dir.Combine("Portable2"), 4000021, dir.Combine("unused")).Create(1, gse: false);
        File.WriteAllText(Path.Combine(fx.BinDirectory, "local_save.txt"), "saves");
        var adapter = new GoldbergAdapter(new TestEnvironment(dir.Combine("appdata")));

        var probe = adapter.Probe(fx.BinDirectory)!;

        Assert.Contains(Path.Combine(fx.BinDirectory, "saves", "4000021", "achievements.json"), probe.StatePaths);
    }
}
