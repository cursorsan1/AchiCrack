using System.Windows;
using Achicrack.App.ViewModels;
using Microsoft.Win32;

namespace Achicrack.App.Views;

public partial class RegisterGameWindow : Window
{
    private readonly RegisterGameViewModel _vm;

    public RegisterGameWindow(RegisterGameViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.Completed += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the game folder" };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.Path = dialog.FolderName;
            _vm.ScanCommand.Execute(null);
        }
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select the game executable", Filter = "Executable (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.Path = dialog.FileName;
            _vm.ScanCommand.Execute(null);
        }
    }
}
