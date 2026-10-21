using System.Threading.Channels;
using Achicrack.Core.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Notifications;

public enum NotificationKind
{
    AchievementUnlocked,
    TrackingStarted,
    Info,
    Test,
}

/// <summary>Renderer-independent description of one popup.</summary>
public sealed record NotificationRequest
{
    public required NotificationKind Kind { get; init; }

    /// <summary>Small caption line, e.g. "Achievement Unlocked" or "Started Tracking".</summary>
    public required string Header { get; init; }

    public required string Title { get; init; }

    public string? Message { get; init; }

    public string? IconPath { get; init; }

    public RarityTier Rarity { get; init; }

    public double? GlobalPercent { get; init; }

    /// <summary>Identical keys within the dedup window are shown only once.</summary>
    public string? DedupKey { get; init; }

    public long? GameId { get; init; }

    public string? ApiName { get; init; }
}

/// <summary>Displays one notification and completes when it has fully disappeared.</summary>
public interface INotificationPresenter
{
    Task PresentAsync(NotificationRequest request, CancellationToken cancellationToken);
}

public sealed class NotificationQueueOptions
{
    public int Capacity { get; init; } = 64;
    public TimeSpan DedupWindow { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Serializes notifications: one popup at a time in arrival order, duplicates suppressed,
/// presenter failures isolated. Detection never waits on rendering.
/// </summary>
public sealed class NotificationQueue : IAsyncDisposable
{
    private readonly INotificationPresenter _presenter;
    private readonly NotificationQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Channel<NotificationRequest> _channel;
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _pending;

    public NotificationQueue(INotificationPresenter presenter, NotificationQueueOptions? options = null,
        TimeProvider? time = null, ILogger<NotificationQueue>? logger = null)
    {
        _presenter = presenter;
        _options = options ?? new NotificationQueueOptions();
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _channel = Channel.CreateBounded<NotificationRequest>(new BoundedChannelOptions(_options.Capacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    /// <summary>Raised after a notification finished presenting (or failed).</summary>
    public event EventHandler<NotificationRequest>? Presented;

    public int PendingCount => Volatile.Read(ref _pending);

    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <returns>False when suppressed as a duplicate.</returns>
    public bool Enqueue(NotificationRequest request)
    {
        if (request.DedupKey is { } key)
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                foreach (var stale in _recent.Where(kv => now - kv.Value > _options.DedupWindow).Select(kv => kv.Key).ToList())
                {
                    _recent.Remove(stale);
                }

                if (_recent.ContainsKey(key))
                {
                    _logger.LogDebug("Duplicate notification suppressed: {Key}", key);
                    return false;
                }

                _recent[key] = now;
            }
        }

        Interlocked.Increment(ref _pending);
        if (!_channel.Writer.TryWrite(request))
        {
            Interlocked.Decrement(ref _pending);
            return false;
        }

        return true;
    }

    public async Task StopAsync()
    {
        _channel.Writer.TryComplete();
        if (_cts is not null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _presenter.PresentAsync(request, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Notification presenter failed for {Title}", request.Title);
                }
                finally
                {
                    Interlocked.Decrement(ref _pending);
                }

                try
                {
                    Presented?.Invoke(this, request);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Presented handler failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
