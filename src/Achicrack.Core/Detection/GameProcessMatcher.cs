using Achicrack.Core.Models;
using Achicrack.Core.Scanning;

namespace Achicrack.Core.Detection;

public enum MatchConfidence
{
    None,

    /// <summary>More than one registered game claims the process equally; not guessed.</summary>
    Ambiguous,

    /// <summary>The executable lives inside a registered install folder and is not a known helper/launcher.</summary>
    InstallDirectory,

    /// <summary>The full executable path equals a registered executable.</summary>
    ExactExecutable,
}

public sealed record ProcessMatch(GameRecord? Game, MatchConfidence Confidence, string Reason)
{
    public static readonly ProcessMatch NoMatch = new(null, MatchConfidence.None, "No registered game matches.");

    public bool IsMatch => Game is not null && Confidence >= MatchConfidence.InstallDirectory;
}

/// <summary>
/// Maps an executable path to a registered game using full paths, never just file names.
/// </summary>
public static class GameProcessMatcher
{
    public static ProcessMatch Match(string? executablePath, IReadOnlyList<GameRecord> games)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new ProcessMatch(null, MatchConfidence.None, "Executable path unavailable; not matched by name.");
        }

        var exe = Normalize(executablePath);
        var enabled = games.Where(g => g.Enabled).ToList();

        var exact = enabled.Where(g => g.Executables.Any(e => Normalize(e) == exe)).ToList();
        if (exact.Count == 1)
        {
            return new ProcessMatch(exact[0], MatchConfidence.ExactExecutable, "Registered executable path.");
        }

        if (exact.Count > 1)
        {
            return new ProcessMatch(null, MatchConfidence.Ambiguous,
                $"Executable registered for {exact.Count} games: {string.Join(", ", exact.Select(g => g.Name))}.");
        }

        var fileName = Path.GetFileName(exe);
        if (GameScanner.IsExcludedExecutable(fileName))
        {
            return new ProcessMatch(null, MatchConfidence.None, $"'{fileName}' is a known launcher/helper process.");
        }

        // Longest containing install directory wins (handles nested installs).
        var containing = enabled
            .Select(g => (Game: g, Dir: Normalize(g.InstallDirectory)))
            .Where(x => exe.StartsWith(x.Dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .GroupBy(x => x.Dir.Length)
            .OrderByDescending(g => g.Key)
            .FirstOrDefault()?
            .ToList();

        if (containing is null || containing.Count == 0)
        {
            return ProcessMatch.NoMatch;
        }

        if (containing.Count > 1)
        {
            return new ProcessMatch(null, MatchConfidence.Ambiguous,
                $"Install folder shared by: {string.Join(", ", containing.Select(x => x.Game.Name))}.");
        }

        return new ProcessMatch(containing[0].Game, MatchConfidence.InstallDirectory, "Executable inside registered install folder.");
    }

    internal static string Normalize(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            full = path;
        }

        // Windows paths are case-insensitive; normalizing case keeps matching consistent.
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    }
}
