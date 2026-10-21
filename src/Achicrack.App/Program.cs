using System.Text;
using System.Windows;

namespace Achicrack.App;

/// <summary>
/// Explicit WPF entry point. It wraps application construction, resource-dictionary loading and
/// <see cref="App.OnStartup"/> in a last-resort handler so a startup failure produces a visible error
/// and a crash file instead of the process dying silently (e.g. after SmartScreen).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException");

        try
        {
            var app = new App();
            app.InitializeComponent(); // loads App.xaml + merged resource dictionaries (can fail before OnStartup)
            app.Run();
        }
        catch (Exception ex)
        {
            WriteCrash(ex, "Startup");
            Environment.Exit(1);
        }
    }

    /// <summary>Writes a crash report to the first writable location and shows it to the user.</summary>
    internal static void WriteCrash(Exception? ex, string origin)
    {
        var text = new StringBuilder()
            .AppendLine($"Achicrack crash report")
            .AppendLine($"Origin : {origin}")
            .AppendLine($"Time   : {DateTimeOffset.Now:O}")
            .AppendLine($"Version: {typeof(Program).Assembly.GetName().Version}")
            .AppendLine($"OS     : {Environment.OSVersion} ({(Environment.Is64BitProcess ? "x64" : "x86")})")
            .AppendLine()
            .AppendLine(ex?.ToString() ?? "(no exception object)")
            .ToString();

        string? writtenPath = null;
        foreach (var dir in CandidateDirectories())
        {
            try
            {
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, $"achicrack-crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                File.WriteAllText(path, text, Encoding.UTF8);
                writtenPath = path;
                break;
            }
            catch
            {
                // Try the next candidate location.
            }
        }

        try
        {
            MessageBox.Show(
                $"Achicrack could not start.\n\n{ex?.Message}\n\nA crash report was written to:\n{writtenPath ?? "(no writable location found)"}",
                "Achicrack startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // No message pump / headless: the crash file above is the record.
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Achicrack");
        yield return Path.Combine(Path.GetTempPath(), "Achicrack");
        yield return AppContext.BaseDirectory;
    }
}
