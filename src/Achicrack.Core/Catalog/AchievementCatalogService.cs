using System.Collections.Concurrent;
using Achicrack.Core.Adapters;
using Achicrack.Core.Events;
using Achicrack.Core.Models;
using Achicrack.Core.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Achicrack.Core.Catalog;

/// <summary>Optional online enrichment (rarity etc.). Never required for offline operation.</summary>
public interface IOnlineMetadataProvider
{
    Task<IReadOnlyDictionary<string, double>?> GetGlobalPercentagesAsync(uint appId, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the complete achievement catalog for a game: adapter-provided local manifest first,
/// then the persisted copy from a previous run. Icons are copied into the local cache.
/// Missing metadata is left null (the UI falls back to the API name) — never invented.
/// </summary>
public sealed class AchievementCatalogService : IAchievementDefinitionLookup
{
    private readonly AdapterRegistry _adapters;
    private readonly AchievementRepository _repository;
    private readonly IconCache _icons;
    private readonly Func<string> _language;
    private readonly IOnlineMetadataProvider? _online;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, AchievementCatalog> _cache = new();

    public AchievementCatalogService(
        AdapterRegistry adapters,
        AchievementRepository repository,
        IconCache icons,
        Func<string>? language = null,
        IOnlineMetadataProvider? online = null,
        ILogger<AchievementCatalogService>? logger = null)
    {
        _adapters = adapters;
        _repository = repository;
        _icons = icons;
        _language = language ?? (() => "english");
        _online = online;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public AchievementCatalog GetCatalog(GameRecord game, bool refresh = false)
    {
        if (!refresh && _cache.TryGetValue(game.Id, out var cached))
        {
            return cached;
        }

        var catalog = LoadFromAdapter(game);
        if (catalog is not null)
        {
            var previous = _repository.LoadCatalog(game.Id);
            catalog = WithCachedIcons(game.Id, catalog, previous);
            _repository.SaveCatalog(game.Id, catalog);
        }
        else
        {
            catalog = _repository.LoadCatalog(game.Id)
                      ?? AchievementCatalog.Empty("No achievement manifest was found for this game. " +
                                                  "Only achievements seen in the state file can be listed.");
        }

        _cache[game.Id] = catalog;
        return catalog;
    }

    public AchievementDefinition? Find(long gameId, string apiName) =>
        _cache.TryGetValue(gameId, out var catalog) ? catalog.Find(apiName) : _repository.LoadCatalog(gameId)?.Find(apiName);

    public void Invalidate(long gameId) => _cache.TryRemove(gameId, out _);

    /// <summary>Fetches global unlock percentages when an online provider is configured. Failures are non-fatal.</summary>
    public async Task<bool> TryEnrichRarityAsync(GameRecord game, CancellationToken cancellationToken)
    {
        if (_online is null || game.AppId is not { } appId)
        {
            return false;
        }

        try
        {
            var percentages = await _online.GetGlobalPercentagesAsync(appId, cancellationToken).ConfigureAwait(false);
            if (percentages is null || percentages.Count == 0)
            {
                return false;
            }

            var catalog = GetCatalog(game);
            var updated = catalog with
            {
                Definitions = catalog.Definitions
                    .Select(d => percentages.TryGetValue(d.ApiName, out var p) ? d with { GlobalPercent = p } : d)
                    .ToArray(),
            };
            _repository.SaveCatalog(game.Id, updated);
            _cache[game.Id] = updated;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Online rarity lookup failed for {Game}", game.Name);
            return false;
        }
    }

    private AchievementCatalog? LoadFromAdapter(GameRecord game)
    {
        var adapter = _adapters.Get(game.AdapterId);
        if (adapter is null)
        {
            return null;
        }

        try
        {
            return adapter.LoadCatalog(game, _language(), _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adapter {Adapter} failed to load catalog for {Game}", adapter.Id, game.Name);
            return null;
        }
    }

    private AchievementCatalog WithCachedIcons(long gameId, AchievementCatalog catalog, AchievementCatalog? previous)
    {
        var defs = catalog.Definitions.Select(d =>
        {
            var old = previous?.Find(d.ApiName);
            return d with
            {
                IconPath = _icons.GetOrAdd(gameId, d.IconPath) ?? ExistingOrNull(old?.IconPath),
                IconGrayPath = _icons.GetOrAdd(gameId, d.IconGrayPath) ?? ExistingOrNull(old?.IconGrayPath),
                GlobalPercent = d.GlobalPercent ?? old?.GlobalPercent,
            };
        }).ToArray();
        return catalog with { Definitions = defs };
    }

    private static string? ExistingOrNull(string? path) => path is not null && File.Exists(path) ? path : null;
}
