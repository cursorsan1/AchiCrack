using System.Collections.Concurrent;
using Achicrack.Core;
using Achicrack.Core.Adapters;
using Achicrack.Core.Detection;
using Achicrack.Core.Infrastructure;
using Achicrack.Core.Notifications;
using Achicrack.Core.Persistence;
using Achicrack.Core.Settings;

namespace Achicrack.Core.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "achicrack-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Manually advanced clock.</summary>
public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public ManualTime() : this(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public sealed class TestEnvironment(string appData) : IUserEnvironment
{
    public string RoamingAppData { get; } = appData;
}

/// <summary>Simulated OS process table.</summary>
public sealed class FakeProcessProvider : IProcessProvider, IForegroundProcessProvider
{
    private readonly ConcurrentDictionary<int, (string Name, string? Path)> _processes = new();
    private int _nextPid = 1000;

    public int? ForegroundPid { get; set; }

    public int PathQueries { get; private set; }

    public int Start(string? executablePath, string? name = null)
    {
        var pid = Interlocked.Increment(ref _nextPid);
        _processes[pid] = (name ?? System.IO.Path.GetFileNameWithoutExtension(executablePath ?? "unknown"), executablePath);
        return pid;
    }

    public void Exit(int pid) => _processes.TryRemove(pid, out _);

    public IReadOnlyList<ProcessInfo> GetProcesses() =>
        _processes.Select(kv => new ProcessInfo(kv.Key, kv.Value.Name)).ToArray();

    public string? TryGetExecutablePath(int processId)
    {
        PathQueries++;
        return _processes.TryGetValue(processId, out var p) ? p.Path : null;
    }

    public int? GetForegroundProcessId() => ForegroundPid;
}

/// <summary>Presenter that records requests and optionally takes time / throws.</summary>
public sealed class RecordingPresenter : INotificationPresenter
{
    private int _concurrent;

    public ConcurrentQueue<NotificationRequest> Shown { get; } = new();

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public Func<NotificationRequest, bool>? ThrowWhen { get; set; }

    public int MaxConcurrent { get; private set; }

    public async Task PresentAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        var now = Interlocked.Increment(ref _concurrent);
        MaxConcurrent = Math.Max(MaxConcurrent, now);
        try
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (ThrowWhen?.Invoke(request) == true)
            {
                throw new InvalidOperationException("presenter failure (test)");
            }

            Shown.Enqueue(request);
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }
}

public static class TestDb
{
    public static AchicrackDatabase Create(TempDir dir, string name = "test.db")
    {
        var db = new AchicrackDatabase(dir.Combine(name));
        db.Initialize();
        return db;
    }
}

public static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000, string? because = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition not met within {timeoutMs} ms. {because}");
            }

            await Task.Delay(20);
        }
    }
}

/// <summary>Full engine with simulated OS services, rooted in a temp folder.</summary>
public sealed class HostFixture : IAsyncDisposable
{
    public HostFixture(TempDir dir, FakeProcessProvider? processes = null, string dataFolder = "data")
    {
        Dir = dir;
        Processes = processes ?? new FakeProcessProvider();
        AppData = dir.Combine("appdata");
        Directory.CreateDirectory(AppData);
        Paths = new AppPaths(dir.Combine(dataFolder));
        Settings = new SettingsStore(Paths.SettingsPath);
        Settings.Save(Settings.Current with
        {
            Monitoring = new MonitoringSettings { ReconcileSeconds = 1, DebounceMilliseconds = 50, ExitGraceSeconds = 0 },
        });
        Host = new AchicrackHost(Paths, Settings, new HostPlatform
        {
            Presenter = Presenter,
            Processes = Processes,
            Foreground = Processes,
            Environment = new TestEnvironment(AppData),
        });
        Host.Start(startDetectionLoop: false);
    }

    public TempDir Dir { get; }
    public string AppData { get; }
    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public FakeProcessProvider Processes { get; }
    public RecordingPresenter Presenter { get; } = new();
    public AchicrackHost Host { get; }

    public async Task PollAsync()
    {
        Host.Detection.PollOnce();
        await Host.AutoTracking.Idle;
    }

    public async ValueTask DisposeAsync() => await Host.DisposeAsync();
}
