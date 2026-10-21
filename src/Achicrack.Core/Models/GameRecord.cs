namespace Achicrack.Core.Models;

/// <summary>
/// A game registered by the user. Registration never modifies anything inside
/// <see cref="InstallDirectory"/>; all values here are derived from a read-only scan.
/// </summary>
public sealed class GameRecord
{
    public long Id { get; set; }

    public required string Name { get; set; }

    /// <summary>Root folder the user selected (or the scanner inferred).</summary>
    public required string InstallDirectory { get; set; }

    /// <summary>Steam App ID when it could be resolved from local files.</summary>
    public uint? AppId { get; set; }

    /// <summary>Human readable description of where <see cref="AppId"/> came from.</summary>
    public string? AppIdSource { get; set; }

    /// <summary>Identifier of the adapter that monitors this game (e.g. "goldberg").</summary>
    public string? AdapterId { get; set; }

    /// <summary>Folder that contains the emulator's steam_settings directory (usually the steam_api DLL folder).</summary>
    public string? SteamApiDirectory { get; set; }

    /// <summary>Executables that identify this game when running (full paths).</summary>
    public List<string> Executables { get; set; } = [];

    /// <summary>Optional user provided cover / icon image.</summary>
    public string? IconPath { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset AddedAtUtc { get; set; }

    public DateTimeOffset? LastPlayedAtUtc { get; set; }

    public override string ToString() => $"{Name} (#{Id}, AppId={AppId?.ToString() ?? "?"})";
}
