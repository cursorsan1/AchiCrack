using Achicrack.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Achicrack.App.ViewModels;

public enum Page
{
    Dashboard,
    CurrentlyPlaying,
    Library,
    Achievements,
    NotificationStudio,
    Settings,
}

public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AchicrackHost host, Func<RegisterGameViewModel, bool> showRegisterDialog)
    {
        Dashboard = new DashboardViewModel(host);
        CurrentlyPlaying = new CurrentlyPlayingViewModel(host);
        Library = new LibraryViewModel(host, showRegisterDialog);
        Achievements = new AchievementsViewModel(host);
        Studio = new NotificationStudioViewModel(host);
        Settings = new SettingsViewModel(host);
        Library.GamesChanged += (_, _) =>
        {
            Achievements.Refresh();
            Dashboard.Refresh();
            CurrentlyPlaying.Refresh();
        };
        host.Detection.GameStarted += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => CurrentPage = Page.CurrentlyPlaying);
    }

    public DashboardViewModel Dashboard { get; }
    public CurrentlyPlayingViewModel CurrentlyPlaying { get; }
    public LibraryViewModel Library { get; }
    public AchievementsViewModel Achievements { get; }
    public NotificationStudioViewModel Studio { get; }
    public SettingsViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboard), nameof(IsCurrentlyPlaying), nameof(IsLibrary), nameof(IsAchievements),
        nameof(IsStudio), nameof(IsSettings))]
    private Page _currentPage = Page.Dashboard;

    public bool IsDashboard { get => CurrentPage == Page.Dashboard; set { if (value) CurrentPage = Page.Dashboard; } }
    public bool IsCurrentlyPlaying { get => CurrentPage == Page.CurrentlyPlaying; set { if (value) CurrentPage = Page.CurrentlyPlaying; } }
    public bool IsLibrary { get => CurrentPage == Page.Library; set { if (value) CurrentPage = Page.Library; } }
    public bool IsAchievements { get => CurrentPage == Page.Achievements; set { if (value) CurrentPage = Page.Achievements; } }
    public bool IsStudio { get => CurrentPage == Page.NotificationStudio; set { if (value) CurrentPage = Page.NotificationStudio; } }
    public bool IsSettings { get => CurrentPage == Page.Settings; set { if (value) CurrentPage = Page.Settings; } }

    public void RefreshAll()
    {
        Dashboard.Refresh();
        CurrentlyPlaying.Refresh();
        Library.Refresh();
        Achievements.Refresh();
    }

    partial void OnCurrentPageChanged(Page value)
    {
        switch (value)
        {
            case Page.Dashboard:
                Dashboard.Refresh();
                break;
            case Page.CurrentlyPlaying:
                CurrentlyPlaying.Refresh();
                break;
            case Page.Library:
                Library.Refresh();
                break;
        }
    }
}
