using System.Diagnostics;
using System.IO;
using Achicrack.App.Infrastructure;
using Achicrack.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace Achicrack.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AchicrackHost _host;

    public SettingsViewModel(AchicrackHost host)
    {
        _host = host;
        Load();
    }

    public IReadOnlyList<string> LogLevels { get; } = ["Trace", "Debug", "Information", "Warning", "Error"];

    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimizedToTray;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _autoDetectGames;
    [ObservableProperty] private bool _showTrackingStartedNotification;
    [ObservableProperty] private bool _showAchievementNotification;
    [ObservableProperty] private bool _onlineMetadataEnabled;
    [ObservableProperty] private string _language = "english";
    [ObservableProperty] private double _processPollSeconds;
    [ObservableProperty] private double _reconcileSeconds;
    [ObservableProperty] private double _exitGraceSeconds;
    [ObservableProperty] private string? _databasePathOverride;
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private string? _status;

    public string DataDirectory => _host.Paths.DataDirectory;

    public string ActiveDatabasePath => _host.Database.DatabasePath;

    public string LogsDirectory => _host.Paths.LogsDirectory;

    [RelayCommand]
    private void Save()
    {
        var before = _host.Settings.Current;
        var after = before with
        {
            StartWithWindows = StartWithWindows,
            StartMinimizedToTray = StartMinimizedToTray,
            CloseToTray = CloseToTray,
            AutoDetectGames = AutoDetectGames,
            ShowTrackingStartedNotification = ShowTrackingStartedNotification,
            ShowAchievementNotification = ShowAchievementNotification,
            OnlineMetadataEnabled = OnlineMetadataEnabled,
            Language = string.IsNullOrWhiteSpace(Language) ? "english" : Language.Trim().ToLowerInvariant(),
            DatabasePathOverride = string.IsNullOrWhiteSpace(DatabasePathOverride) ? null : DatabasePathOverride.Trim(),
            LogLevel = LogLevel,
            Monitoring = before.Monitoring with
            {
                ProcessPollSeconds = Math.Clamp(ProcessPollSeconds, 0.5, 30),
                ReconcileSeconds = Math.Clamp(ReconcileSeconds, 1, 300),
                ExitGraceSeconds = Math.Clamp(ExitGraceSeconds, 0, 120),
            },
        };
        _host.Settings.Save(after);

        try
        {
            StartupRegistration.Apply(after.StartWithWindows);
            Status = "Saved.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Status = $"Saved, but autostart could not be changed: {ex.Message}";
        }

        var restartNeeded = before.DatabasePathOverride != after.DatabasePathOverride ||
                            before.Monitoring.ProcessPollSeconds != after.Monitoring.ProcessPollSeconds ||
                            before.Monitoring.ExitGraceSeconds != after.Monitoring.ExitGraceSeconds ||
                            before.AutoDetectGames != after.AutoDetectGames ||
                            before.OnlineMetadataEnabled != after.OnlineMetadataEnabled ||
                            before.LogLevel != after.LogLevel;
        if (restartNeeded)
        {
            Status += " Some changes take effect after restarting Achicrack.";
        }

        Load();
    }

    [RelayCommand]
    private void BrowseDatabase()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "SQLite database (*.db)|*.db",
            FileName = "achicrack.db",
            OverwritePrompt = false,
        };
        if (dialog.ShowDialog() == true)
        {
            DatabasePathOverride = dialog.FileName;
        }
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(DataDirectory);

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(LogsDirectory);

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void Load()
    {
        var s = _host.Settings.Current;
        StartWithWindows = s.StartWithWindows;
        StartMinimizedToTray = s.StartMinimizedToTray;
        CloseToTray = s.CloseToTray;
        AutoDetectGames = s.AutoDetectGames;
        ShowTrackingStartedNotification = s.ShowTrackingStartedNotification;
        ShowAchievementNotification = s.ShowAchievementNotification;
        OnlineMetadataEnabled = s.OnlineMetadataEnabled;
        Language = s.Language;
        ProcessPollSeconds = s.Monitoring.ProcessPollSeconds;
        ReconcileSeconds = s.Monitoring.ReconcileSeconds;
        ExitGraceSeconds = s.Monitoring.ExitGraceSeconds;
        DatabasePathOverride = s.DatabasePathOverride;
        LogLevel = s.LogLevel;
    }
}
