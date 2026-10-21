using System.Windows.Media;
using Achicrack.App.Infrastructure;
using Achicrack.App.Overlay;
using Achicrack.Core.Library;
using Achicrack.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Achicrack.App.ViewModels;

public sealed partial class AchievementItemViewModel : ObservableObject
{
    private readonly AchievementListItem _source;

    public AchievementItemViewModel(AchievementListItem item, RarityColors colors)
    {
        _source = item;
        ApiName = item.ApiName;
        Title = item.Title;
        HasKnownName = item.DisplayName is not null;
        _isUnlocked = item.IsUnlocked;
        _unlockedAt = item.UnlockedAtUtc;
        RarityText = item.Rarity == RarityTier.Unknown
            ? null
            : item.GlobalPercent is { } p ? $"{Rarity.Label(item.Rarity)} · {p:0.#}%" : Rarity.Label(item.Rarity);
        RarityBrush = NotificationCardModel.BrushFrom(NotificationCardModel.RarityColor(colors, item.Rarity), Colors.Silver);
        Description = item.Description ?? (item.Hidden ? "Hidden achievement" : item.InCatalog ? null : "Not listed in the achievement catalog");
    }

    public string ApiName { get; }

    public string Title { get; }

    public bool HasKnownName { get; }

    public string? Description { get; }

    public string? RarityText { get; }

    public Brush RarityBrush { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(UnlockedText), nameof(StateText))]
    private bool _isUnlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnlockedText))]
    private DateTimeOffset? _unlockedAt;

    /// <summary>True only for unlocks that happened during this session (drives the one-time highlight animation).</summary>
    [ObservableProperty] private bool _isNewlyUnlocked;

    /// <summary>Unlocked: full-color icon. Locked: gray icon from metadata, else the desaturated color icon.</summary>
    public ImageSource? Icon => IsUnlocked
        ? ImageLoader.Load(_source.IconPath)
        : ImageLoader.Load(_source.IconGrayPath) ?? ImageLoader.Load(_source.IconPath, grayscale: true);

    public string? UnlockedText => IsUnlocked && UnlockedAt is { } t ? $"Unlocked {t.ToLocalTime():yyyy-MM-dd HH:mm}" : null;

    public string StateText => IsUnlocked ? "Unlocked" : "Locked";

    public bool Matches(string? search) =>
        string.IsNullOrWhiteSpace(search) ||
        Title.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
        ApiName.Contains(search, StringComparison.OrdinalIgnoreCase);

    public void MarkUnlocked(DateTimeOffset at)
    {
        UnlockedAt = at;
        IsUnlocked = true;
        IsNewlyUnlocked = true;
        _ = ClearHighlightLaterAsync();
    }

    /// <summary>Ends the "new" state so recycled list containers do not replay the highlight.</summary>
    private async Task ClearHighlightLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(4));
        IsNewlyUnlocked = false;
    }
}
