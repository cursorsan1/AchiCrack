using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Settings;

/// <summary>JSON settings persisted atomically; a corrupt file is backed up and replaced with defaults.</summary>
public sealed class SettingsStore(string path, ILogger? logger = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILogger _logger = logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private AppSettings? _current;

    public string FilePath { get; } = path;

    public event EventHandler<AppSettings>? Changed;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= LoadFromDisk();
            }
        }
    }

    public void Save(AppSettings settings)
    {
        settings = settings with { Notification = settings.Notification.Normalized() };
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
            _current = settings;
        }

        Changed?.Invoke(this, settings);
    }

    public void Update(Func<AppSettings, AppSettings> change) => Save(change(Current));

    private AppSettings LoadFromDisk()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
            return settings is null ? new AppSettings() : settings with { Notification = settings.Notification.Normalized() };
        }
        catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
        {
            var backup = FilePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Copy(FilePath, backup, overwrite: true);
            }
            catch (IOException)
            {
            }

            _logger.LogWarning(ex, "Settings file {Path} is invalid; using defaults (backup: {Backup})", FilePath, backup);
            return new AppSettings();
        }
    }
}
