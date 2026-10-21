namespace Achicrack.Core.Settings;

public enum NotificationPosition
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight,
}

public enum NotificationAnimation
{
    /// <summary>Slides in horizontally from the nearest screen edge (PS5-like).</summary>
    Slide,

    /// <summary>Drops in vertically from the nearest edge.</summary>
    Drop,

    Fade,

    /// <summary>Scales up slightly while fading in.</summary>
    Pop,
}

/// <summary>Appearance and behavior of the overlay popup. Everything here is user-customizable.</summary>
public sealed record NotificationStyle
{
    public NotificationPosition Position { get; init; } = NotificationPosition.TopRight;
    public double DurationSeconds { get; init; } = 5.0;
    public double Scale { get; init; } = 1.0;
    public double BackgroundOpacity { get; init; } = 0.86;
    public NotificationAnimation Animation { get; init; } = NotificationAnimation.Slide;
    public double MarginX { get; init; } = 32;
    public double MarginY { get; init; } = 32;
    public string FontFamily { get; init; } = "Segoe UI";
    public string BackgroundColor { get; init; } = "#1B1D22";
    public string AccentColor { get; init; } = "#3D8BFF";
    public bool ShowRarity { get; init; } = true;
    public bool SoundEnabled { get; init; } = true;

    /// <summary>Custom .wav/.mp3 path; null uses the built-in chime.</summary>
    public string? SoundPath { get; init; }

    public double SoundVolume { get; init; } = 0.6;

    /// <summary>Custom icon for "Started Tracking" popups when a game has no icon.</summary>
    public string? CustomTrackingIcon { get; init; }

    public RarityColors RarityColors { get; init; } = new();

    public NotificationStyle Normalized() => this with
    {
        DurationSeconds = Math.Clamp(DurationSeconds, 1.5, 30),
        Scale = Math.Clamp(Scale, 0.5, 2.5),
        BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0.2, 1.0),
        SoundVolume = Math.Clamp(SoundVolume, 0, 1),
        MarginX = Math.Clamp(MarginX, 0, 400),
        MarginY = Math.Clamp(MarginY, 0, 400),
        FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? "Segoe UI" : FontFamily,
    };
}

/// <summary>Trophy-tier colors (PS-style bronze / silver / gold / platinum) mapped to rarity.</summary>
public sealed record RarityColors
{
    public string Common { get; init; } = "#C27A4A";   // bronze
    public string Rare { get; init; } = "#C9CED6";     // silver
    public string VeryRare { get; init; } = "#E8C25A"; // gold
    public string UltraRare { get; init; } = "#9FD6F2"; // platinum
    public string Unknown { get; init; } = "#C9CED6";
}

public sealed record NotificationPreset(string Name, NotificationStyle Style);

public sealed record MonitoringSettings
{
    public double ProcessPollSeconds { get; init; } = 2;
    public double ExitGraceSeconds { get; init; } = 5;
    public double ReconcileSeconds { get; init; } = 10;
    public int DebounceMilliseconds { get; init; } = 250;
}

public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public bool StartWithWindows { get; init; }
    public bool StartMinimizedToTray { get; init; } = true;
    public bool CloseToTray { get; init; } = true;
    public bool AutoDetectGames { get; init; } = true;
    public bool ShowTrackingStartedNotification { get; init; } = true;
    public bool ShowAchievementNotification { get; init; } = true;

    /// <summary>Steam schema language used when reading localized manifests.</summary>
    public string Language { get; init; } = "english";

    /// <summary>Opt-in: fetch global rarity from the public Steam Web API.</summary>
    public bool OnlineMetadataEnabled { get; init; }

    /// <summary>Custom database file; null uses the default under the data folder.</summary>
    public string? DatabasePathOverride { get; init; }

    public string LogLevel { get; init; } = "Information";
    public MonitoringSettings Monitoring { get; init; } = new();
    public NotificationStyle Notification { get; init; } = new();
    public List<NotificationPreset> Presets { get; init; } = [];
}
