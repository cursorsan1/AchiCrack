using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Achicrack.App.Infrastructure;
using Achicrack.Core;
using Achicrack.Core.Models;
using Achicrack.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Achicrack.App.ViewModels;

public sealed partial class GameItemViewModel(GameRecord game) : ObservableObject
{
    public GameRecord Game { get; } = game;
    public string Name => Game.Name;
    public string AppIdText => Game.AppId is { } id ? $"App ID {id}" : "App ID unknown";
    public string MethodText => Game.AdapterId switch
    {
        "goldberg" => "Goldberg / GSE state file",
        "simulated" => "Simulated (demo)",
        null => "No supported method",
        var other => other,
    };
    public string InstallDirectory => Game.InstallDirectory;
    public ImageSource? Icon => ImageLoader.Load(Game.IconPath);

    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private string _achievementText = "";
    [ObservableProperty] private bool _isTracking;
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AchicrackHost _host;
    private readonly Func<RegisterGameViewModel, bool> _showRegisterDialog;

    public LibraryViewModel(AchicrackHost host, Func<RegisterGameViewModel, bool> showRegisterDialog)
    {
        _host = host;
        _showRegisterDialog = showRegisterDialog;
        host.Coordinator.SessionChanged += (_, _) => Application.Current?.Dispatcher.InvokeAsync(UpdateStatuses);
        host.Engine.AchievementUnlocked += (_, _) => Application.Current?.Dispatcher.InvokeAsync(UpdateStatuses);
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveGameCommand), nameof(ToggleTrackingCommand))]
    private GameItemViewModel? _selected;

    /// <summary>Raised after games were added or removed.</summary>
    public event EventHandler? GamesChanged;

    public void Refresh()
    {
        var selectedId = Selected?.Game.Id;
        Games.Clear();
        foreach (var g in _host.Games.GetAll())
        {
            Games.Add(new GameItemViewModel(g));
        }

        UpdateStatuses();
        Selected = Games.FirstOrDefault(g => g.Game.Id == selectedId);
    }

    [RelayCommand]
    private void AddGame()
    {
        var vm = new RegisterGameViewModel(_host);
        if (_showRegisterDialog(vm) && vm.RegisteredGame is not null)
        {
            _host.Detection.InvalidateGames();
            Refresh();
            Selected = Games.FirstOrDefault(g => g.Game.Id == vm.RegisteredGame.Id);
            GamesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void AddDemoGame()
    {
        _host.EnsureDemoGame();
        Refresh();
        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task RemoveGame()
    {
        if (Selected is not { } item)
        {
            return;
        }

        var answer = MessageBox.Show(
            $"Remove '{item.Name}' from Achicrack?\n\nIts recorded achievement history in Achicrack is deleted as well. " +
            "Nothing in the game folder is touched.",
            "Remove game", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await _host.Coordinator.StopTrackingAsync(item.Game.Id);
        _host.Games.Delete(item.Game.Id);
        _host.Engine.Forget(item.Game.Id);
        _host.Catalogs.Invalidate(item.Game.Id);
        _host.IconCache.Purge(item.Game.Id);
        _host.Detection.InvalidateGames();
        Refresh();
        GamesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Manual start/stop (useful for games whose process cannot be matched, and for testing).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ToggleTracking()
    {
        if (Selected is not { } item)
        {
            return;
        }

        var session = _host.Coordinator.GetSession(item.Game.Id);
        if (session?.State == TrackingState.TrackingActive)
        {
            await _host.Coordinator.StopTrackingAsync(item.Game.Id);
        }
        else
        {
            await _host.Coordinator.StartTrackingAsync(item.Game);
        }

        UpdateStatuses();
    }

    private bool HasSelection() => Selected is not null;

    private void UpdateStatuses()
    {
        var stats = _host.Achievements.GetStats().ToDictionary(s => s.GameId);
        foreach (var item in Games)
        {
            var session = _host.Coordinator.GetSession(item.Game.Id);
            item.StatusText = session is null ? "Idle" : CurrentlyPlayingViewModel.Label(session.State);
            item.IsTracking = session?.State == TrackingState.TrackingActive;
            item.StatusBrush = session?.State switch
            {
                TrackingState.TrackingActive => (Brush)Application.Current.FindResource("SuccessBrush"),
                TrackingState.TrackingUnavailable => (Brush)Application.Current.FindResource("DangerBrush"),
                _ => Brushes.Gray,
            };
            if (stats.TryGetValue(item.Game.Id, out var s))
            {
                item.AchievementText = s.CatalogComplete && s.CatalogTotal > 0
                    ? $"{s.Unlocked} / {s.CatalogTotal} achievements"
                    : $"{s.Unlocked} unlocked (catalog unknown)";
            }
        }
    }
}
