namespace Achicrack.Core.Adapters;

public sealed class AdapterRegistry(IEnumerable<IGameAdapter> adapters)
{
    private readonly IReadOnlyList<IGameAdapter> _adapters = adapters.ToArray();

    public IReadOnlyList<IGameAdapter> All => _adapters;

    public IGameAdapter? Get(string? id) =>
        id is null ? null : _adapters.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));
}
