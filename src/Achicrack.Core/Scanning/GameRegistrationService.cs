using Achicrack.Core.Models;
using Achicrack.Core.Persistence;

namespace Achicrack.Core.Scanning;

/// <summary>Turns a <see cref="ScanReport"/> (plus optional user overrides) into a registered game.</summary>
public sealed class GameRegistrationService(GameRepository games, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public GameRecord Register(ScanReport report, string? nameOverride = null, uint? appIdOverride = null,
        IReadOnlyList<string>? executables = null)
    {
        var existing = FindByInstallDirectory(report.InstallDirectory);
        if (existing is not null)
        {
            throw new InvalidOperationException($"'{existing.Name}' is already registered for this folder.");
        }

        var exeList = (executables ?? report.Executables.Take(1).ToList()).ToList();
        var game = new GameRecord
        {
            Name = string.IsNullOrWhiteSpace(nameOverride) ? report.SuggestedName : nameOverride.Trim(),
            InstallDirectory = report.InstallDirectory,
            AppId = appIdOverride ?? report.AppId,
            AppIdSource = appIdOverride is not null ? "Entered manually" : report.AppIdSource,
            AdapterId = report.SelectedProbe?.AdapterId,
            SteamApiDirectory = report.SelectedProbe?.SteamApiDirectory,
            Executables = exeList,
            AddedAtUtc = _time.GetUtcNow(),
        };
        games.Add(game);
        return game;
    }

    public GameRecord? FindByInstallDirectory(string directory)
    {
        var normalized = Normalize(directory);
        return games.GetAll().FirstOrDefault(g => Normalize(g.InstallDirectory) == normalized);
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
}
