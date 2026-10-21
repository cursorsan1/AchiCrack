using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Achicrack.App.Infrastructure;
using Achicrack.Core;
using Achicrack.Core.Models;
using Achicrack.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Achicrack.App.ViewModels;

public sealed record RunningGameChip(long GameId, string Name, string State, bool IsPrimary);

/// <summary>"Currently Playing": the primary running game, its tracking state and complete achievement list.</summary>
public sealed partial class CurrentlyPlayingViewModel : ObservableObject
{
    private readonly AchicrackHost _host;

    public CurrentlyPlayingViewModel(AchicrackHost host)
    {
        _host = host;
        Achievements = new GameAchievementsViewModel(host);
        host.Coordinator.SessionChanged += (_, s) => Ui(() => OnSessionChanged(s));
        host.Detection.PrimaryGameChanged += (_, _) => Ui(Refresh);
        host.Engine.AchievementUnlocked += (_, e) => Ui(() => Achievements.ApplyUnlock(e.Unlock));
        host.Detection.AmbiguousProcess += (_, u) => Ui(() => UnknownGameNotice =
            $"Unknown game: '{Path.GetFileName(u.ExecutablePath)}' could not be identified confidently ({u.Reason}) and is not tracked.");
        host.Detection.GameStarted += (_, _) => Ui(() => UnknownGameNotice = null);
    }

    /// <summary>Shown when a running process could belong to several registered games (never guessed).</summary>
    [ObservableProperty] private string? _unknownGameNotice;

    public GameAchievementsViewModel Achievements { get; }

    public ObservableCollection<RunningGameChip> RunningGames { get; } = [];

    [ObservableProperty] private string _gameName = "No game running";
    [ObservableProperty] private string _statusText = "Inactive";
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private ImageSource? _gameIcon;
    [ObservableProperty] private string? _watchedPaths;
    [ObservableProperty] private bool _hasGame;

    /// <summary>Rebuilds the view from current detection/tracking state. UI thread only.</summary>
    public void Refresh()
    {
        var sessions = _host.Coordinator.Sessions.ToDictionary(s => s.Game.Id);
        var primaryId = _host.Detection.PrimaryGameId;

        RunningGames.Clear();
        foreach (var running in _host.Detection.RunningGames)
        {
            sessions.TryGetValue(running.Game.Id, out var s);
            RunningGames.Add(new RunningGameChip(running.Game.Id, running.Game.Name, Label(s?.State), running.Game.Id == primaryId));
        }

        // Simulated demo sessions have no process; show them when nothing real is running.
        primaryId ??= sessions.Values.FirstOrDefault(s => s.State == TrackingState.TrackingActive)?.Game.Id;

        if (primaryId is { } id && (sessions.TryGetValue(id, out var session) || _host.Games.Get(id) is not null))
        {
            var game = session?.Game ?? _host.Games.Get(id)!;
            Show(game, session, active: true);
            return;
        }

        var recent = _host.Games.GetAll()
            .Where(g => g.LastPlayedAtUtc is not null)
            .OrderByDescending(g => g.LastPlayedAtUtc)
            .FirstOrDefault();
        if (recent is null)
        {
            HasGame = false;
            GameName = "No game running";
            StatusText = "Inactive";
            StatusDetail = "Launch a registered game and Achicrack will start tracking it automatically.";
            StatusBrush = Brushes.Gray;
            IsActive = false;
            GameIcon = null;
            WatchedPaths = null;
            Achievements.Load(null);
            return;
        }

        Show(recent, null, active: false);
        StatusText = "Inactive";
        StatusDetail = $"Last played {recent.LastPlayedAtUtc!.Value.ToLocalTime():yyyy-MM-dd HH:mm}. Not running now.";
        StatusBrush = Brushes.Gray;
    }

    private void OnSessionChanged(TrackingSessionInfo info)
    {
        var shown = Achievements.Game?.Id;
        if (shown == info.Game.Id || _host.Detection.PrimaryGameId == info.Game.Id || shown is null ||
            info.State == TrackingState.TrackingActive)
        {
            Refresh();
        }
    }

    private void Show(GameRecord game, TrackingSessionInfo? session, bool active)
    {
        HasGame = true;
        GameName = game.Name;
        GameIcon = ImageLoader.Load(game.IconPath);
        IsActive = active && session?.State == TrackingState.TrackingActive;
        StatusText = Label(session?.State);
        StatusDetail = session?.StatusMessage;
        StatusBrush = session?.State switch
        {
            TrackingState.TrackingActive => (Brush)Application.Current.FindResource("SuccessBrush"),
            TrackingState.TrackingUnavailable => (Brush)Application.Current.FindResource("DangerBrush"),
            TrackingState.GameDetected or TrackingState.InitializingTracker => (Brush)Application.Current.FindResource("WarningBrush"),
            _ => Brushes.Gray,
        };
        WatchedPaths = session?.WatchedPaths is { Count: > 0 } paths ? string.Join(Environment.NewLine, paths) : null;
        if (Achievements.Game?.Id != game.Id || session?.State == TrackingState.TrackingActive)
        {
            Achievements.Load(game);
        }
    }

    internal static string Label(TrackingState? state) => state switch
    {
        TrackingState.GameDetected => "Game Detected",
        TrackingState.InitializingTracker => "Initializing Tracker",
        TrackingState.TrackingActive => "Tracking Active",
        TrackingState.TrackingUnavailable => "Tracking Unavailable",
        TrackingState.TrackingStopped => "Tracking Stopped",
        _ => "Not tracked",
    };

    private static void Ui(Action action) => Application.Current?.Dispatcher.InvokeAsync(action);
}
