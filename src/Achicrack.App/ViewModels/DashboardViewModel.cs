using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Achicrack.App.Infrastructure;
using Achicrack.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Achicrack.App.ViewModels;

public sealed record RecentUnlockItem(string Title, string? Description, string GameName, string When, ImageSource? Icon);

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AchicrackHost _host;

    public DashboardViewModel(AchicrackHost host)
    {
        _host = host;
        host.Engine.AchievementUnlocked += (_, _) => Application.Current?.Dispatcher.InvokeAsync(Refresh);
    }

    public ObservableCollection<RecentUnlockItem> RecentUnlocks { get; } = [];

    [ObservableProperty] private int _gameCount;
    [ObservableProperty] private int _totalUnlocked;
    [ObservableProperty] private string _totalKnown = "–";
    [ObservableProperty] private string _completion = "–";

    public void Refresh()
    {
        var games = _host.Games.GetAll();
        var stats = _host.Achievements.GetStats();
        GameCount = games.Count;
        TotalUnlocked = stats.Sum(s => s.Unlocked);

        var complete = stats.Where(s => s.CatalogComplete && s.CatalogTotal > 0).ToList();
        var known = complete.Sum(s => s.CatalogTotal);
        TotalKnown = complete.Count == 0 ? "–" : known.ToString(System.Globalization.CultureInfo.CurrentCulture);
        Completion = known > 0 ? $"{100.0 * complete.Sum(s => Math.Min(s.Unlocked, s.CatalogTotal)) / known:0.0}%" : "–";

        RecentUnlocks.Clear();
        foreach (var r in _host.Achievements.GetRecentUnlocks(12))
        {
            RecentUnlocks.Add(new RecentUnlockItem(
                r.DisplayName ?? r.Unlock.ApiName,
                r.Description,
                r.GameName,
                r.Unlock.UnlockedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.CurrentCulture),
                ImageLoader.Load(r.IconPath)));
        }
    }
}
