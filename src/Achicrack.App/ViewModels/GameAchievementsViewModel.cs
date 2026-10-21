using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Achicrack.Core;
using Achicrack.Core.Library;
using Achicrack.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Achicrack.App.ViewModels;

public enum AchievementFilter
{
    All,
    Locked,
    Unlocked,
}

/// <summary>Complete achievement list of one game (locked + unlocked) with filter, search and live updates.</summary>
public sealed partial class GameAchievementsViewModel : ObservableObject
{
    private readonly AchicrackHost _host;

    public GameAchievementsViewModel(AchicrackHost host)
    {
        _host = host;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o is AchievementItemViewModel item && PassesFilter(item);
    }

    public ObservableCollection<AchievementItemViewModel> Items { get; } = [];

    public ICollectionView View { get; }

    [ObservableProperty] private GameRecord? _game;
    [ObservableProperty] private string _progressText = "–";
    [ObservableProperty] private string _completionText = "–";
    [ObservableProperty] private double _completionValue;
    [ObservableProperty] private string? _lastAchievement;
    [ObservableProperty] private string? _catalogNote;
    [ObservableProperty] private bool _isCatalogComplete;
    [ObservableProperty] private string _catalogSource = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsLocked), nameof(IsUnlocked))]
    private AchievementFilter _filter = AchievementFilter.All;

    [ObservableProperty] private string? _searchText;

    public bool IsAll
    {
        get => Filter == AchievementFilter.All;
        set { if (value) Filter = AchievementFilter.All; }
    }

    public bool IsLocked
    {
        get => Filter == AchievementFilter.Locked;
        set { if (value) Filter = AchievementFilter.Locked; }
    }

    public bool IsUnlocked
    {
        get => Filter == AchievementFilter.Unlocked;
        set { if (value) Filter = AchievementFilter.Unlocked; }
    }

    /// <summary>Loads (or reloads) the full list. Must run on the UI thread.</summary>
    public void Load(GameRecord? game)
    {
        Game = game;
        Items.Clear();
        if (game is null)
        {
            UpdateSummary(null);
            return;
        }

        var catalog = _host.Catalogs.GetCatalog(game);
        var summary = AchievementListBuilder.Build(catalog, _host.Achievements.GetUnlocks(game.Id));
        var colors = _host.Settings.Current.Notification.RarityColors;
        foreach (var item in summary.Items)
        {
            Items.Add(new AchievementItemViewModel(item, colors));
        }

        UpdateSummary(summary);
    }

    /// <summary>Applies a live unlock in place (no reload, so only this item animates).</summary>
    public void ApplyUnlock(UnlockRecord unlock)
    {
        if (Game is null || unlock.GameId != Game.Id)
        {
            return;
        }

        var item = Items.FirstOrDefault(i => i.ApiName == unlock.ApiName);
        if (item is null)
        {
            Load(Game); // unknown to the catalog: rebuild so it is listed by API name
            Items.FirstOrDefault(i => i.ApiName == unlock.ApiName)?.MarkUnlocked(unlock.UnlockedAtUtc);
            return;
        }

        if (!item.IsUnlocked)
        {
            item.MarkUnlocked(unlock.UnlockedAtUtc);
        }

        var catalog = _host.Catalogs.GetCatalog(Game);
        UpdateSummary(AchievementListBuilder.Build(catalog, _host.Achievements.GetUnlocks(Game.Id)));
        View.Refresh();
    }

    partial void OnFilterChanged(AchievementFilter value) => View.Refresh();

    partial void OnSearchTextChanged(string? value) => View.Refresh();

    private bool PassesFilter(AchievementItemViewModel item) =>
        item.Matches(SearchText) && Filter switch
        {
            AchievementFilter.Locked => !item.IsUnlocked,
            AchievementFilter.Unlocked => item.IsUnlocked,
            _ => true,
        };

    private void UpdateSummary(GameAchievementSummary? summary)
    {
        if (summary is null)
        {
            ProgressText = "–";
            CompletionText = "–";
            CompletionValue = 0;
            LastAchievement = null;
            CatalogNote = null;
            CatalogSource = "";
            IsCatalogComplete = false;
            return;
        }

        IsCatalogComplete = summary.IsCatalogComplete;
        CatalogSource = summary.CatalogSource;
        ProgressText = summary.Total is { } total ? $"{summary.Unlocked} / {total}" : $"{summary.Unlocked} / ?";
        CompletionText = summary.CompletionPercent is { } pct ? $"{pct:0.0}%" : "unknown";
        CompletionValue = summary.CompletionPercent ?? 0;
        LastAchievement = summary.LastUnlocked?.Title;
        CatalogNote = summary.IsCatalogComplete
            ? null
            : "The achievement list is incomplete: no full achievement catalog was found for this game. " +
              "Only achievements seen as unlocked are listed, and missing entries are not assumed to be locked.";
    }
}
