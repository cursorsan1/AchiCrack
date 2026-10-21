using System.Windows;
using System.Windows.Media;
using Achicrack.App.Infrastructure;
using Achicrack.Core.Library;
using Achicrack.Core.Notifications;
using Achicrack.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Achicrack.App.Overlay;

/// <summary>Everything the notification card renders. Shared by the overlay and the Notification Studio preview.</summary>
public sealed partial class NotificationCardModel : ObservableObject
{
    [ObservableProperty] private string _header = "Achievement Unlocked";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string? _message;
    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] private bool _isTracking;
    [ObservableProperty] private string? _rarityText;
    [ObservableProperty] private Brush _trophyBrush = Brushes.Gold;
    [ObservableProperty] private Brush _accentBrush = Brushes.DodgerBlue;
    [ObservableProperty] private Brush _backgroundBrush = Brushes.Black;
    [ObservableProperty] private FontFamily _fontFamily = new("Segoe UI");
    [ObservableProperty] private double _scale = 1.0;

    public Visibility MessageVisibility => string.IsNullOrWhiteSpace(Message) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility RarityVisibility => string.IsNullOrWhiteSpace(RarityText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TrophyGlyphVisibility => IsTracking ? Visibility.Collapsed : Visibility.Visible;

    public Visibility GamepadGlyphVisibility => IsTracking ? Visibility.Visible : Visibility.Collapsed;

    public static NotificationCardModel From(NotificationRequest request, NotificationStyle style)
    {
        var model = new NotificationCardModel();
        model.Apply(request, style);
        return model;
    }

    public void Apply(NotificationRequest request, NotificationStyle style)
    {
        Header = request.Header;
        Title = request.Title;
        Message = request.Message;
        IsTracking = request.Kind == NotificationKind.TrackingStarted;
        var iconPath = request.IconPath ?? (IsTracking ? style.CustomTrackingIcon : null);
        Icon = ImageLoader.Load(iconPath);
        RarityText = style.ShowRarity && request.Rarity != RarityTier.Unknown
            ? request.GlobalPercent is { } p ? $"{Rarity.Label(request.Rarity)} · {p:0.#}%" : Rarity.Label(request.Rarity)
            : null;
        TrophyBrush = BrushFrom(RarityColor(style.RarityColors, request.Rarity), Colors.Gold);
        ApplyStyle(style);
    }

    public void ApplyStyle(NotificationStyle style)
    {
        AccentBrush = BrushFrom(style.AccentColor, Colors.DodgerBlue);
        var bg = ColorFrom(style.BackgroundColor, Color.FromRgb(0x1B, 0x1D, 0x22));
        var top = Color.FromArgb((byte)(255 * style.BackgroundOpacity), bg.R, bg.G, bg.B);
        var bottom = Color.FromArgb((byte)(255 * Math.Min(1, style.BackgroundOpacity + 0.06)),
            (byte)(bg.R * 0.8), (byte)(bg.G * 0.8), (byte)(bg.B * 0.8));
        var gradient = new LinearGradientBrush(top, bottom, 90);
        gradient.Freeze();
        BackgroundBrush = gradient;
        Scale = style.Scale;
        try
        {
            FontFamily = new FontFamily(style.FontFamily);
        }
        catch (ArgumentException)
        {
            FontFamily = new FontFamily("Segoe UI");
        }
    }

    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(MessageVisibility));

    partial void OnRarityTextChanged(string? value) => OnPropertyChanged(nameof(RarityVisibility));

    partial void OnIsTrackingChanged(bool value)
    {
        OnPropertyChanged(nameof(TrophyGlyphVisibility));
        OnPropertyChanged(nameof(GamepadGlyphVisibility));
    }

    internal static string RarityColor(RarityColors colors, RarityTier tier) => tier switch
    {
        RarityTier.Common => colors.Common,
        RarityTier.Rare => colors.Rare,
        RarityTier.VeryRare => colors.VeryRare,
        RarityTier.UltraRare => colors.UltraRare,
        _ => colors.Unknown,
    };

    internal static Color ColorFrom(string? value, Color fallback)
    {
        try
        {
            return value is null ? fallback : (Color)ColorConverter.ConvertFromString(value);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            return fallback;
        }
    }

    internal static SolidColorBrush BrushFrom(string? value, Color fallback)
    {
        var brush = new SolidColorBrush(ColorFrom(value, fallback));
        brush.Freeze();
        return brush;
    }
}
