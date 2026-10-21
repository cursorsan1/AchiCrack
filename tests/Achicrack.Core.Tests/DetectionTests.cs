using Achicrack.Core.Detection;
using Achicrack.Core.Models;

namespace Achicrack.Core.Tests;

public class GameProcessMatcherTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "games");

    private static GameRecord Game(long id, string folder, params string[] exes) => new()
    {
        Id = id,
        Name = folder,
        InstallDirectory = Path.Combine(Root, folder),
        Executables = exes.Select(e => Path.Combine(Root, folder, e)).ToList(),
    };

    [Fact]
    public void Exact_registered_path_wins()
    {
        var games = new[] { Game(1, "Alpha", "bin", "Alpha.exe") };
        games[0].Executables = [Path.Combine(Root, "Alpha", "bin", "Alpha.exe")];
        var match = GameProcessMatcher.Match(Path.Combine(Root, "ALPHA", "BIN", "alpha.EXE"), games);
        Assert.Equal(MatchConfidence.ExactExecutable, match.Confidence);
        Assert.Equal(1, match.Game!.Id);
    }

    [Fact]
    public void Executable_inside_install_folder_matches()
    {
        var match = GameProcessMatcher.Match(Path.Combine(Root, "Alpha", "bin", "x64", "Other.exe"), [Game(1, "Alpha")]);
        Assert.Equal(MatchConfidence.InstallDirectory, match.Confidence);
        Assert.True(match.IsMatch);
    }

    [Fact]
    public void Same_file_name_in_another_folder_does_not_match()
    {
        var games = new[] { Game(1, "Alpha", "Game.exe") };
        var match = GameProcessMatcher.Match(Path.Combine(Root, "Beta", "Game.exe"), games);
        Assert.False(match.IsMatch);
    }

    [Fact]
    public void Launchers_and_helpers_are_not_mistaken_for_the_game()
    {
        var games = new[] { Game(1, "Alpha", "Alpha.exe") };
        Assert.False(GameProcessMatcher.Match(Path.Combine(Root, "Alpha", "Launcher.exe"), games).IsMatch);
        Assert.False(GameProcessMatcher.Match(Path.Combine(Root, "Alpha", "UnityCrashHandler64.exe"), games).IsMatch);
        Assert.False(GameProcessMatcher.Match(Path.Combine(Root, "Alpha", "redist", "vc_redist.x64.exe"), games).IsMatch);
    }

    [Fact]
    public void Explicitly_registered_launcher_name_still_matches_exactly()
    {
        var games = new[] { Game(1, "Alpha", "launcher.exe") };
        Assert.Equal(MatchConfidence.ExactExecutable,
            GameProcessMatcher.Match(Path.Combine(Root, "Alpha", "launcher.exe"), games).Confidence);
    }

    [Fact]
    public void Missing_path_is_never_matched_by_name()
    {
        var match = GameProcessMatcher.Match(null, [Game(1, "Alpha", "Alpha.exe")]);
        Assert.False(match.IsMatch);
    }

    [Fact]
    public void Nested_install_folders_pick_the_most_specific()
    {
        var games = new[] { Game(1, "Collection"), Game(2, Path.Combine("Collection", "Part2")) };
        var match = GameProcessMatcher.Match(Path.Combine(Root, "Collection", "Part2", "p2.exe"), games);
        Assert.Equal(2, match.Game!.Id);
    }

    [Fact]
    public void Ambiguous_registrations_are_not_guessed()
    {
        var games = new[] { Game(1, "Shared"), Game(2, "Shared") };
        var match = GameProcessMatcher.Match(Path.Combine(Root, "Shared", "x.exe"), games);
        Assert.Equal(MatchConfidence.Ambiguous, match.Confidence);
        Assert.Null(match.Game);
    }

    [Fact]
    public void Disabled_games_are_ignored()
    {
        var game = Game(1, "Alpha", "Alpha.exe");
        game.Enabled = false;
        Assert.False(GameProcessMatcher.Match(game.Executables[0], [game]).IsMatch);
    }
}

public class GameDetectionServiceTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "games-detect");

    private static GameRecord Game(long id, string name) => new()
    {
        Id = id,
        Name = name,
        InstallDirectory = Path.Combine(Root, name),
        Executables = [Path.Combine(Root, name, name + ".exe")],
    };

    private sealed class Harness
    {
        public Harness(params GameRecord[] games)
        {
            Games = games.ToList();
            Service = new GameDetectionService(Processes, () => Games, Processes,
                new GameDetectionOptions { ExitGracePeriod = TimeSpan.FromSeconds(5) }, Time);
            Service.GameStarted += (_, g) => Started.Add(g.Game.Name);
            Service.GameStopped += (_, g) => Stopped.Add(g.Game.Name);
            Service.PrimaryGameChanged += (_, id) => PrimaryChanges.Add(id);
        }

        public List<GameRecord> Games { get; }
        public FakeProcessProvider Processes { get; } = new();
        public ManualTime Time { get; } = new();
        public GameDetectionService Service { get; }
        public List<string> Started { get; } = [];
        public List<string> Stopped { get; } = [];
        public List<long?> PrimaryChanges { get; } = [];

        public void Poll(double advanceSeconds = 2)
        {
            Time.Advance(TimeSpan.FromSeconds(advanceSeconds));
            Service.PollOnce();
        }
    }

    [Fact]
    public void Detects_start_and_exit_after_grace_period()
    {
        var h = new Harness(Game(1, "Alpha"));
        h.Poll();
        Assert.Empty(h.Started);

        var pid = h.Processes.Start(h.Games[0].Executables[0]);
        h.Poll();
        Assert.Equal(["Alpha"], h.Started);
        Assert.Single(h.Service.RunningGames);

        h.Processes.Exit(pid);
        h.Poll(1);
        Assert.Empty(h.Stopped); // within grace period
        h.Poll(5);
        Assert.Equal(["Alpha"], h.Stopped);
        Assert.Empty(h.Service.RunningGames);
    }

    [Fact]
    public void Multiple_processes_of_one_game_start_it_once()
    {
        var h = new Harness(Game(1, "Alpha"));
        var exe = h.Games[0].Executables[0];
        var p1 = h.Processes.Start(exe);
        var p2 = h.Processes.Start(Path.Combine(Root, "Alpha", "helper", "worker.exe"));
        h.Poll();
        var p3 = h.Processes.Start(exe);
        h.Poll();

        Assert.Equal(["Alpha"], h.Started);
        h.Processes.Exit(p1);
        h.Processes.Exit(p3);
        h.Poll(10);
        Assert.Empty(h.Stopped); // worker still running
        h.Processes.Exit(p2);
        h.Poll(1);
        h.Poll(10);
        Assert.Equal(["Alpha"], h.Stopped);
    }

    [Fact]
    public void Relaunch_within_grace_period_is_not_a_new_session()
    {
        var h = new Harness(Game(1, "Alpha"));
        var exe = h.Games[0].Executables[0];
        var pid = h.Processes.Start(exe);
        h.Poll();
        h.Processes.Exit(pid);
        h.Poll(1);
        h.Processes.Start(exe);
        h.Poll(1);
        h.Poll(10);

        Assert.Equal(["Alpha"], h.Started);
        Assert.Empty(h.Stopped);
    }

    [Fact]
    public void Unregistered_processes_are_ignored_and_paths_queried_once()
    {
        var h = new Harness(Game(1, "Alpha"));
        h.Processes.Start(Path.Combine(Root, "Other", "Other.exe"));
        h.Processes.Start(null, "System");
        h.Poll();
        var queries = h.Processes.PathQueries;
        h.Poll();
        h.Poll();

        Assert.Empty(h.Started);
        Assert.Equal(queries, h.Processes.PathQueries); // paths are only resolved for new processes
    }

    [Fact]
    public void Foreground_game_becomes_primary_and_survives_window_switches()
    {
        var h = new Harness(Game(1, "Alpha"), Game(2, "Beta"));
        var a = h.Processes.Start(h.Games[0].Executables[0]);
        var b = h.Processes.Start(h.Games[1].Executables[0]);
        h.Processes.ForegroundPid = b;
        h.Poll();
        Assert.Equal(2, h.Service.PrimaryGameId);
        Assert.Equal(2, h.Service.RunningGames.Count);

        h.Processes.ForegroundPid = a;
        h.Poll();
        Assert.Equal(1, h.Service.PrimaryGameId);

        h.Processes.ForegroundPid = 999_999; // user alt-tabs to a browser
        h.Poll();
        Assert.Equal(1, h.Service.PrimaryGameId);
        Assert.Equal(2, h.Service.RunningGames.Count); // background monitoring continues

        h.Processes.Exit(a);
        h.Poll(1);
        h.Poll(10);
        Assert.Equal(2, h.Service.PrimaryGameId);
    }

    [Fact]
    public void Newly_registered_game_is_detected_while_already_running()
    {
        var h = new Harness();
        var exe = Path.Combine(Root, "Late", "Late.exe");
        h.Processes.Start(exe);
        h.Poll();
        Assert.Empty(h.Started);

        h.Games.Add(Game(3, "Late"));
        h.Service.InvalidateGames();
        h.Poll();

        Assert.Equal(["Late"], h.Started);
    }

    [Fact]
    public void Removing_a_running_game_stops_it()
    {
        var h = new Harness(Game(1, "Alpha"));
        h.Processes.Start(h.Games[0].Executables[0]);
        h.Poll();
        h.Games.Clear();
        h.Service.InvalidateGames();
        h.Poll();
        Assert.Equal(["Alpha"], h.Stopped);
    }

    [Fact]
    public void Ambiguous_process_is_reported_not_started()
    {
        var shared = Path.Combine(Root, "Shared");
        var h = new Harness(
            new GameRecord { Id = 1, Name = "One", InstallDirectory = shared },
            new GameRecord { Id = 2, Name = "Two", InstallDirectory = shared });
        var ambiguous = new List<UnmatchedProcess>();
        h.Service.AmbiguousProcess += (_, u) => ambiguous.Add(u);
        h.Processes.Start(Path.Combine(shared, "game.exe"));
        h.Poll();

        Assert.Empty(h.Started);
        Assert.Single(ambiguous);
    }
}
