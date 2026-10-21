using System.Collections.ObjectModel;
using System.Windows;
using Achicrack.Core;
using Achicrack.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Achicrack.App.ViewModels;

/// <summary>Browse the achievement list of any registered game.</summary>
public sealed partial class AchievementsViewModel : ObservableObject
{
    private readonly AchicrackHost _host;

    public AchievementsViewModel(AchicrackHost host)
    {
        _host = host;
        List = new GameAchievementsViewModel(host);
        host.Engine.AchievementUnlocked += (_, e) => Application.Current?.Dispatcher.InvokeAsync(() => List.ApplyUnlock(e.Unlock));
    }

    public ObservableCollection<GameRecord> Games { get; } = [];

    public GameAchievementsViewModel List { get; }

    [ObservableProperty] private GameRecord? _selectedGame;

    public void Refresh()
    {
        var selectedId = SelectedGame?.Id;
        Games.Clear();
        foreach (var g in _host.Games.GetAll())
        {
            Games.Add(g);
        }

        SelectedGame = Games.FirstOrDefault(g => g.Id == selectedId) ?? Games.FirstOrDefault();
        List.Load(SelectedGame);
    }

    partial void OnSelectedGameChanged(GameRecord? value) => List.Load(value);
}
