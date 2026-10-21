using Achicrack.Core.Adapters;

namespace Achicrack.Core.Scanning;

public sealed record DetectionMethodReport(string MethodName, DetectionStatus Status, string Details, string? AdapterId = null);

public sealed record ScanReport
{
    public required string ScannedPath { get; init; }
    public required string InstallDirectory { get; init; }
    public required string SuggestedName { get; init; }
    public uint? AppId { get; init; }
    public string? AppIdSource { get; init; }
    public IReadOnlyList<string> SteamApiDlls { get; init; } = [];
    public IReadOnlyList<string> Executables { get; init; } = [];

    /// <summary>The probe chosen for monitoring (best status), if any.</summary>
    public AdapterProbeResult? SelectedProbe { get; init; }

    public IReadOnlyList<AdapterProbeResult> Probes { get; init; } = [];
    public IReadOnlyList<DetectionMethodReport> Methods { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool Truncated { get; init; }

    public bool CanMonitor => SelectedProbe is { Status: not DetectionStatus.Unsupported, AppId: not null };
}

/// <summary>
/// Read-only scan of a game folder: finds steam_api DLLs, emulator markers, App IDs and candidate
/// executables, and lets each adapter report whether it can monitor the game. Never writes anything.
/// </summary>
public sealed class GameScanner(AdapterRegistry adapters)
{
    public const int DefaultMaxDepth = 5;
    public const int DefaultMaxEntries = 50_000;

    private static readonly string[] SteamApiDllNames = ["steam_api64.dll", "steam_api.dll"];

    /// <summary>Folder names that are not a useful game name.</summary>
    private static readonly HashSet<string> GenericFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "binaries", "win64", "win32", "x64", "x86", "x86_64", "game", "retail", "release", "shipping", "windows",
        "windowsnoeditor", "_retail_", "common",
    };

    /// <summary>Executables that are never the game itself (launchers, crash handlers, installers...).</summary>
    public static readonly IReadOnlySet<string> ExcludedExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "unitycrashhandler64.exe", "unitycrashhandler32.exe", "crashreportclient.exe", "crashpad_handler.exe",
        "unins000.exe", "unins001.exe", "uninstall.exe", "setup.exe", "vc_redist.x64.exe", "vc_redist.x86.exe",
        "vcredist_x64.exe", "vcredist_x86.exe", "dxsetup.exe", "dxwebsetup.exe", "dotnetfx.exe", "ue4prereqsetup_x64.exe",
        "ueprereqsetup_x64.exe", "easyanticheat_setup.exe", "easyanticheat_eos_setup.exe", "launcher.exe",
        "redlauncher.exe", "steamworks_setup.exe", "oalinst.exe", "physx.exe", "crashreporter.exe", "errorreporter.exe",
        "bugsplat.exe", "bssndrpt64.exe", "quicksfv.exe", "7z.exe", "cefprocess.exe", "cefsharp.browsersubprocess.exe",
        "zfgamebrowser.exe", "unrealcefsubprocess.exe", "epicwebhelper.exe",
    };

    public static bool IsExcludedExecutable(string fileName) =>
        ExcludedExecutables.Contains(fileName) ||
        fileName.Contains("crash", StringComparison.OrdinalIgnoreCase) ||
        fileName.Contains("redist", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase);

    public ScanReport Scan(string path, int maxDepth = DefaultMaxDepth, int maxEntries = DefaultMaxEntries)
    {
        var fullPath = Path.GetFullPath(path);
        string root;
        string? selectedExe = null;
        if (File.Exists(fullPath) && fullPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            selectedExe = fullPath;
            root = InferInstallRoot(Path.GetDirectoryName(fullPath)!);
        }
        else if (Directory.Exists(fullPath))
        {
            root = fullPath;
        }
        else
        {
            throw new DirectoryNotFoundException($"'{path}' is neither a folder nor an executable.");
        }

        var warnings = new List<string>();
        var dlls = new List<string>();
        var exes = new List<string>();
        var truncated = Walk(root, maxDepth, maxEntries, dlls, exes, warnings);

        var probes = new List<AdapterProbeResult>();
        foreach (var dllDir in dlls.Select(d => Path.GetDirectoryName(d)!).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var adapter in adapters.All)
            {
                try
                {
                    if (adapter.Probe(dllDir) is { } probe)
                    {
                        probes.Add(probe);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"{adapter.DisplayName} probe failed in {dllDir}: {ex.Message}");
                }
            }
        }

        var selected = probes
            .OrderBy(p => p.Status)
            .ThenByDescending(p => p.AppId is not null)
            .ThenByDescending(p => p.CatalogPath is not null)
            .FirstOrDefault();

        uint? appId = selected?.AppId;
        var appIdSource = selected?.AppIdSource;
        if (appId is null)
        {
            var exeDirs = exes.Select(e => Path.GetDirectoryName(e)!).Prepend(root).Distinct(StringComparer.OrdinalIgnoreCase);
            (appId, appIdSource) = SteamAppIdResolver.Resolve(root, exeDirs);
        }

        var rankedExes = RankExecutables(exes, root, selectedExe);
        return new ScanReport
        {
            ScannedPath = fullPath,
            InstallDirectory = root,
            SuggestedName = SuggestName(root),
            AppId = appId,
            AppIdSource = appIdSource,
            SteamApiDlls = dlls,
            Executables = rankedExes,
            SelectedProbe = selected,
            Probes = probes,
            Methods = BuildMethodReports(dlls, probes),
            Warnings = warnings,
            Truncated = truncated,
        };
    }

    internal static string InferInstallRoot(string exeDirectory)
    {
        // Walk up out of generic folders such as bin\x64 or Binaries\Win64.
        var dir = new DirectoryInfo(exeDirectory);
        while (dir.Parent is not null && GenericFolderNames.Contains(dir.Name))
        {
            dir = dir.Parent;
        }

        return dir.FullName;
    }

    internal static string SuggestName(string root)
    {
        var dir = new DirectoryInfo(root);
        while (dir.Parent is not null && GenericFolderNames.Contains(dir.Name))
        {
            dir = dir.Parent;
        }

        return dir.Name;
    }

    private static IReadOnlyList<string> RankExecutables(List<string> exes, string root, string? selected)
    {
        var rootName = SuggestName(root).Replace(" ", "", StringComparison.Ordinal);
        var ranked = exes
            .Where(e => !IsExcludedExecutable(Path.GetFileName(e)))
            .OrderByDescending(e => string.Equals(e, selected, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => Path.GetFileNameWithoutExtension(e)
                .Replace(" ", "", StringComparison.Ordinal)
                .Contains(rootName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => SafeLength(e))
            .ToList();
        if (selected is not null && !ranked.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            ranked.Insert(0, selected);
        }

        return ranked;
    }

    private static long SafeLength(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static List<DetectionMethodReport> BuildMethodReports(List<string> dlls, List<AdapterProbeResult> probes)
    {
        var methods = new List<DetectionMethodReport>();
        if (dlls.Count == 0)
        {
            methods.Add(new DetectionMethodReport("Steamworks API (steam_api DLL)", DetectionStatus.Unsupported,
                "No steam_api.dll / steam_api64.dll found. The game does not appear to use Steamworks."));
        }

        foreach (var probe in probes)
        {
            methods.Add(new DetectionMethodReport(probe.MethodName, probe.Status, probe.Details, probe.AdapterId));
        }

        methods.Add(new DetectionMethodReport("Steamworks API call observation (SetAchievement / StoreStats)",
            DetectionStatus.Unsupported,
            "Requires DLL replacement or code injection. Intentionally not implemented in this release (research topic)."));
        methods.Add(new DetectionMethodReport("Official Steam client",
            DetectionStatus.Unsupported,
            "Achievements managed by the real Steam client are stored server-side and are not monitored."));
        return methods;
    }

    /// <returns>True when the walk stopped early because of <paramref name="maxEntries"/>.</returns>
    private static bool Walk(string root, int maxDepth, int maxEntries, List<string> dlls, List<string> exes, List<string> warnings)
    {
        var count = 0;
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((root, 0));
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        };

        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var entry in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
                {
                    if (++count > maxEntries)
                    {
                        warnings.Add($"Scan stopped after {maxEntries} entries; deep folders may be missed.");
                        return true;
                    }

                    if (entry is DirectoryInfo sub)
                    {
                        if (depth < maxDepth)
                        {
                            queue.Enqueue((sub.FullName, depth + 1));
                        }
                    }
                    else if (SteamApiDllNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        dlls.Add(entry.FullName);
                    }
                    else if (entry.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exes.Add(entry.FullName);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not read {dir}: {ex.Message}");
            }
        }

        return false;
    }
}
