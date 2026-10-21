using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using Achicrack.Core;
using Achicrack.Core.Adapters;
using Achicrack.Core.Models;
using Achicrack.Core.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Achicrack.App.ViewModels;

public sealed record MethodRow(string Name, string Status, string Details, Brush Brush);

public sealed partial class ExecutableOption(string path, bool selected) : ObservableObject
{
    public string Path { get; } = path;

    [ObservableProperty] private bool _isSelected = selected;
}

/// <summary>Scan → review compatibility → register. Scanning is read-only.</summary>
public sealed partial class RegisterGameViewModel(AchicrackHost host) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string _path = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterCommand))]
    private ScanReport? _report;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _appIdText = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _summary;
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<MethodRow> Methods { get; } = [];

    public ObservableCollection<ExecutableOption> Executables { get; } = [];

    public GameRecord? RegisteredGame { get; private set; }

    public event EventHandler? Completed;

    [RelayCommand(CanExecute = nameof(CanScan))]
    private async Task Scan()
    {
        Error = null;
        IsBusy = true;
        try
        {
            var path = Path.Trim().Trim('"');
            var report = await Task.Run(() => host.Scanner.Scan(path));
            Report = report;
            Name = report.SuggestedName;
            AppIdText = report.AppId?.ToString(CultureInfo.InvariantCulture) ?? "";
            Methods.Clear();
            foreach (var m in report.Methods)
            {
                Methods.Add(new MethodRow(m.MethodName, m.Status.ToString(), m.Details, m.Status switch
                {
                    DetectionStatus.Supported => new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97)),
                    DetectionStatus.Unverified => new SolidColorBrush(Color.FromRgb(0xF5, 0xB8, 0x41)),
                    _ => new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xAA)),
                }));
            }

            Executables.Clear();
            for (var i = 0; i < report.Executables.Count; i++)
            {
                Executables.Add(new ExecutableOption(report.Executables[i], i == 0));
            }

            Summary = report.CanMonitor
                ? $"Monitoring available ({report.SelectedProbe!.Status}). App ID {report.AppId} from {report.AppIdSource}."
                : "No verified monitoring method found. You can still register the game; tracking will report 'Unavailable'.";
            if (report.Warnings.Count > 0)
            {
                Summary += $" {report.Warnings.Count} scan warning(s).";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Report = null;
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRegister))]
    private void Register()
    {
        if (Report is null)
        {
            return;
        }

        uint? appId = null;
        if (!string.IsNullOrWhiteSpace(AppIdText))
        {
            if (!uint.TryParse(AppIdText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
            {
                Error = "App ID must be a positive number.";
                return;
            }

            appId = parsed;
        }

        try
        {
            var exes = Executables.Where(e => e.IsSelected).Select(e => e.Path).ToList();
            RegisteredGame = host.Registration.Register(Report, Name,
                appId is not null && appId != Report.AppId ? appId : null, exes);
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
        }
    }

    private bool CanScan() => !string.IsNullOrWhiteSpace(Path);

    private bool CanRegister() => Report is not null;
}
