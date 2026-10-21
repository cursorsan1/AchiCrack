using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Achicrack.App.Infrastructure;

/// <summary>
/// Structured diagnostic log: one JSON object per line in logs\achicrack-YYYYMMDD.jsonl,
/// written by a background thread so logging never blocks monitoring or the UI.
/// </summary>
internal sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private const int RetainedFiles = 14;
    private readonly string _directory;
    private readonly BlockingCollection<string> _lines = new(boundedCapacity: 10_000);
    private readonly Thread _writer;

    public JsonFileLoggerProvider(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        PruneOldFiles();
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "Achicrack log writer" };
        _writer.Start();
    }

    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    public ILogger CreateLogger(string categoryName) => new JsonFileLogger(this, categoryName);

    public void Dispose()
    {
        _lines.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
    }

    internal void Enqueue(string line)
    {
        if (_lines.IsAddingCompleted)
        {
            return;
        }

        try
        {
            _lines.TryAdd(line);
        }
        catch (InvalidOperationException)
        {
            // Completed concurrently during shutdown: drop the line rather than crash.
        }
    }

    private void WriteLoop()
    {
        StreamWriter? writer = null;
        var currentDay = DateOnly.MinValue;
        try
        {
            foreach (var line in _lines.GetConsumingEnumerable())
            {
                var today = DateOnly.FromDateTime(DateTime.Now);
                if (writer is null || today != currentDay)
                {
                    writer?.Dispose();
                    currentDay = today;
                    var path = Path.Combine(_directory, $"achicrack-{today:yyyyMMdd}.jsonl");
                    writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }

                writer.WriteLine(line);
            }
        }
        catch (IOException)
        {
            // Logging must never take the application down.
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private void PruneOldFiles()
    {
        try
        {
            foreach (var old in Directory.GetFiles(_directory, "achicrack-*.jsonl").Order().SkipLast(RetainedFiles))
            {
                File.Delete(old);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class JsonFileLogger(JsonFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider.MinimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var entry = new Dictionary<string, object?>
            {
                ["ts"] = DateTimeOffset.Now.ToString("O"),
                ["level"] = logLevel.ToString(),
                ["category"] = category,
                ["message"] = formatter(state, exception),
            };
            if (state is IReadOnlyList<KeyValuePair<string, object?>> props)
            {
                foreach (var (key, value) in props)
                {
                    if (key != "{OriginalFormat}")
                    {
                        entry["p_" + key] = value?.ToString();
                    }
                }
            }

            if (exception is not null)
            {
                entry["exception"] = exception.ToString();
            }

            provider.Enqueue(JsonSerializer.Serialize(entry));
        }
    }
}
