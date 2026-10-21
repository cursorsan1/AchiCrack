using System.ComponentModel;
using System.Windows;
using Achicrack.App.ViewModels;

namespace Achicrack.App.Views;

public partial class MainWindow : Window
{
    private readonly Func<bool> _closeToTray;

    public MainWindow(MainViewModel viewModel, Func<bool> closeToTray)
    {
        InitializeComponent();
        DataContext = viewModel;
        _closeToTray = closeToTray;
    }

    /// <summary>Set when the application is really exiting (tray "Exit").</summary>
    public bool AllowClose { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose && _closeToTray())
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
