using System.Collections.ObjectModel;
using System.Windows.Media;
using Achicrack.App.Overlay;
using Achicrack.Core;
using Achicrack.Core.Library;
using Achicrack.Core.Notifications;
using Achicrack.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Achicrack.App.ViewModels;

public enum PreviewKind
{
    Achievement,
    Tracking,
}

/// <summary>Live preview and persistence of the notification appearance.</summary>
public sealed partial class NotificationStudioViewModel : ObservableObject
{
    private readonly AchicrackHost _host;
    private bool _loading;

    public NotificationStudioViewModel(AchicrackHost host)
    {
        _host = host;
        Fonts = new ObservableCollection<string>(System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order());
        LoadFrom(host.Settings.Current.Notification);
        RefreshPresets();
    }

    public IReadOnlyList<NotificationPosition> Positions { get; } = Enum.GetValues<NotificationPosition>();
    public IReadOnlyList<NotificationAnimation> Animations { get; } = Enum.GetValues<NotificationAnimation>();
    public IReadOnlyList<RarityTier> RarityTiers { get; } = Enum.GetValues<RarityTier>();
    public IReadOnlyList<PreviewKind> PreviewKinds { get; } = Enum.GetValues<PreviewKind>();
    public ObservableCollection<string> Fonts { get; }
    public ObservableCollection<string> Presets { get; } = [];

    public NotificationCardModel Preview { get; } = new();

    [ObservableProperty] private NotificationPosition _position;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _scale;
    [ObservableProperty] private double _backgroundOpacity;
    [ObservableProperty] private NotificationAnimation _animation;
    [ObservableProperty] private string _fontFamily = "Segoe UI";
    [ObservableProperty] private string _backgroundColor = "#1B1D22";
    [ObservableProperty] private string _accentColor = "#3D8BFF";
    [ObservableProperty] private double _marginX;
    [ObservableProperty] private double _marginY;
    [ObservableProperty] private bool _showRarity;
    [ObservableProperty] private bool _soundEnabled;
    [ObservableProperty] private string? _soundPath;
    [ObservableProperty] private double _soundVolume;
    [ObservableProperty] private RarityTier _previewRarity = RarityTier.VeryRare;
    [ObservableProperty] private PreviewKind _previewKind = PreviewKind.Achievement;
    [ObservableProperty] private string _previewTitle = "The Beginning";
    [ObservableProperty] private string _previewMessage = "Complete the first mission.";
    [ObservableProperty] private string? _selectedPreset;
    [ObservableProperty] private string _newPresetName = "";
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _isDirty;

    public NotificationStyle BuildStyle() => (_host.Settings.Current.Notification with
    {
        Position = Position,
        DurationSeconds = DurationSeconds,
        Scale = Scale,
        BackgroundOpacity = BackgroundOpacity,
        Animation = Animation,
        FontFamily = FontFamily,
        BackgroundColor = BackgroundColor,
        AccentColor = AccentColor,
        MarginX = MarginX,
        MarginY = MarginY,
        ShowRarity = ShowRarity,
        SoundEnabled = SoundEnabled,
        SoundPath = string.IsNullOrWhiteSpace(SoundPath) ? null : SoundPath,
        SoundVolume = SoundVolume,
    }).Normalized();

    [RelayCommand]
    private void Save()
    {
        _host.Settings.Update(s => s with { Notification = BuildStyle() });
        IsDirty = false;
        Status = "Saved.";
    }

    [RelayCommand]
    private void Reset()
    {
        LoadFrom(new NotificationStyle());
        IsDirty = true;
        Status = "Defaults loaded (not saved yet).";
    }

    /// <summary>Shows the current (unsaved) style through the real overlay and queue.</summary>
    [RelayCommand]
    private void TestNotification()
    {
        var saved = _host.Settings.Current;
        // The presenter reads the persisted style, so apply the preview style for this test.
        _host.Settings.Save(saved with { Notification = BuildStyle() });
        IsDirty = false;
        _host.Queue.Enqueue(BuildPreviewRequest() with { DedupKey = null });
        Status = "Test notification queued (current style saved).";
    }

    [RelayCommand]
    private async Task SimulateUnlock()
    {
        Save();
        var api = await _host.SimulateDemoUnlockAsync();
        Status = api is null
            ? "All demo achievements are already unlocked."
            : $"Simulated unlock of {api} on the demo game (full pipeline: engine → database → overlay).";
    }

    [RelayCommand]
    private void BrowseSound()
    {
        var dialog = new OpenFileDialog { Filter = "Audio (*.wav;*.mp3)|*.wav;*.mp3|All files|*.*" };
        if (dialog.ShowDialog() == true)
        {
            SoundPath = dialog.FileName;
        }
    }

    [RelayCommand]
    private void ClearSound() => SoundPath = null;

    [RelayCommand]
    private void SavePreset()
    {
        var name = NewPresetName.Trim();
        if (name.Length == 0)
        {
            Status = "Enter a preset name.";
            return;
        }

        var style = BuildStyle();
        _host.Settings.Update(s => s with
        {
            Presets = s.Presets.Where(p => p.Name != name).Append(new NotificationPreset(name, style)).ToList(),
        });
        RefreshPresets();
        SelectedPreset = name;
        Status = $"Preset '{name}' saved.";
    }

    [RelayCommand]
    private void ApplyPreset()
    {
        var preset = _host.Settings.Current.Presets.FirstOrDefault(p => p.Name == SelectedPreset);
        if (preset is null)
        {
            return;
        }

        LoadFrom(preset.Style);
        IsDirty = true;
        Status = $"Preset '{preset.Name}' loaded (press Save to make it active).";
    }

    [RelayCommand]
    private void DeletePreset()
    {
        if (SelectedPreset is not { } name)
        {
            return;
        }

        _host.Settings.Update(s => s with { Presets = s.Presets.Where(p => p.Name != name).ToList() });
        RefreshPresets();
        Status = $"Preset '{name}' deleted.";
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(Status) or nameof(IsDirty) or nameof(SelectedPreset) or nameof(NewPresetName))
        {
            return;
        }

        if (e.PropertyName is not (nameof(PreviewKind) or nameof(PreviewRarity) or nameof(PreviewTitle) or nameof(PreviewMessage)))
        {
            IsDirty = true;
        }

        UpdatePreview();
    }

    private NotificationRequest BuildPreviewRequest()
    {
        var percent = PreviewRarity switch
        {
            RarityTier.Common => 64.2,
            RarityTier.Rare => 31.5,
            RarityTier.VeryRare => 12.8,
            RarityTier.UltraRare => 2.1,
            _ => (double?)null,
        };
        return PreviewKind == PreviewKind.Tracking
            ? new NotificationRequest
            {
                Kind = NotificationKind.TrackingStarted,
                Header = "Started Tracking",
                Title = PreviewTitle,
                Message = "Achievement tracking is now active.",
            }
            : new NotificationRequest
            {
                Kind = NotificationKind.Test,
                Header = "Achievement Unlocked",
                Title = PreviewTitle,
                Message = PreviewMessage,
                Rarity = PreviewRarity,
                GlobalPercent = percent,
            };
    }

    private void UpdatePreview() => Preview.Apply(BuildPreviewRequest(), BuildStyle());

    private void LoadFrom(NotificationStyle style)
    {
        _loading = true;
        try
        {
            Position = style.Position;
            DurationSeconds = style.DurationSeconds;
            Scale = style.Scale;
            BackgroundOpacity = style.BackgroundOpacity;
            Animation = style.Animation;
            FontFamily = style.FontFamily;
            BackgroundColor = style.BackgroundColor;
            AccentColor = style.AccentColor;
            MarginX = style.MarginX;
            MarginY = style.MarginY;
            ShowRarity = style.ShowRarity;
            SoundEnabled = style.SoundEnabled;
            SoundPath = style.SoundPath;
            SoundVolume = style.SoundVolume;
        }
        finally
        {
            _loading = false;
        }

        UpdatePreview();
    }

    private void RefreshPresets()
    {
        Presets.Clear();
        foreach (var p in _host.Settings.Current.Presets)
        {
            Presets.Add(p.Name);
        }
    }

    internal static Brush Swatch(string color) => NotificationCardModel.BrushFrom(color, Colors.Gray);
}
