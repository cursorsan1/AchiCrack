using Achicrack.Core.Models;
using Microsoft.Data.Sqlite;

namespace Achicrack.Core.Persistence;

public sealed record RecentUnlock(UnlockRecord Unlock, string GameName, string? DisplayName, string? Description, string? IconPath);

public sealed record GameAchievementStats(long GameId, int Unlocked, int CatalogTotal, bool CatalogComplete);

public sealed class AchievementRepository(AchicrackDatabase database)
{
    /// <summary>Inserts an unlock unless one already exists. Returns true only for a genuinely new row.</summary>
    public bool TryInsertUnlock(UnlockRecord unlock)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO unlocks (game_id, api_name, unlocked_at, unlock_time_source, observed_at, source, is_baseline)
            VALUES ($game, $api, $unlocked, $fromSource, $observed, $source, $baseline);
            """;
        cmd.Parameters.AddWithValue("$game", unlock.GameId);
        cmd.Parameters.AddWithValue("$api", unlock.ApiName);
        cmd.Parameters.AddWithValue("$unlocked", AchicrackDatabase.ToUnixMs(unlock.UnlockedAtUtc));
        cmd.Parameters.AddWithValue("$fromSource", unlock.UnlockTimeFromSource ? 1 : 0);
        cmd.Parameters.AddWithValue("$observed", AchicrackDatabase.ToUnixMs(unlock.ObservedAtUtc));
        cmd.Parameters.AddWithValue("$source", unlock.Source);
        cmd.Parameters.AddWithValue("$baseline", unlock.IsBaseline ? 1 : 0);
        return cmd.ExecuteNonQuery() == 1;
    }

    public void MarkNotified(long gameId, string apiName, DateTimeOffset when)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE unlocks SET notified_at=$when WHERE game_id=$game AND api_name=$api;";
        cmd.Parameters.AddWithValue("$when", AchicrackDatabase.ToUnixMs(when));
        cmd.Parameters.AddWithValue("$game", gameId);
        cmd.Parameters.AddWithValue("$api", apiName);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<string, UnlockRecord> GetUnlocks(long gameId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT game_id, api_name, unlocked_at, unlock_time_source, observed_at, source, is_baseline
            FROM unlocks WHERE game_id=$game;
            """;
        cmd.Parameters.AddWithValue("$game", gameId);
        using var reader = cmd.ExecuteReader();
        var result = new Dictionary<string, UnlockRecord>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var record = ReadUnlock(reader, 0);
            result[record.ApiName] = record;
        }

        return result;
    }

    public IReadOnlyList<RecentUnlock> GetRecentUnlocks(int limit)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT u.game_id, u.api_name, u.unlocked_at, u.unlock_time_source, u.observed_at, u.source, u.is_baseline,
                   g.name, d.display_name, d.description, d.icon_path
            FROM unlocks u
            JOIN games g ON g.id = u.game_id
            LEFT JOIN achievement_definitions d ON d.game_id = u.game_id AND d.api_name = u.api_name
            ORDER BY u.unlocked_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var result = new List<RecentUnlock>();
        while (reader.Read())
        {
            result.Add(new RecentUnlock(
                ReadUnlock(reader, 0),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10)));
        }

        return result;
    }

    public void SaveCatalog(long gameId, AchievementCatalog catalog)
    {
        using var connection = database.Open();
        using var tx = connection.BeginTransaction();

        using (var del = connection.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM achievement_definitions WHERE game_id=$game; DELETE FROM catalogs WHERE game_id=$game;";
            del.Parameters.AddWithValue("$game", gameId);
            del.ExecuteNonQuery();
        }

        using (var meta = connection.CreateCommand())
        {
            meta.Transaction = tx;
            meta.CommandText = """
                INSERT INTO catalogs (game_id, source, is_complete, notes, retrieved_at)
                VALUES ($game, $source, $complete, $notes, $retrieved);
                """;
            meta.Parameters.AddWithValue("$game", gameId);
            meta.Parameters.AddWithValue("$source", catalog.Source);
            meta.Parameters.AddWithValue("$complete", catalog.IsComplete ? 1 : 0);
            meta.Parameters.AddWithValue("$notes", (object?)catalog.Notes ?? DBNull.Value);
            meta.Parameters.AddWithValue("$retrieved", AchicrackDatabase.ToUnixMs(catalog.RetrievedAtUtc));
            meta.ExecuteNonQuery();
        }

        using (var ins = connection.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT OR REPLACE INTO achievement_definitions
                    (game_id, api_name, display_name, description, hidden, icon_path, icon_gray_path, global_percent, sort_order)
                VALUES ($game, $api, $name, $desc, $hidden, $icon, $gray, $pct, $order);
                """;
            var pGame = ins.Parameters.Add("$game", SqliteType.Integer);
            var pApi = ins.Parameters.Add("$api", SqliteType.Text);
            var pName = ins.Parameters.Add("$name", SqliteType.Text);
            var pDesc = ins.Parameters.Add("$desc", SqliteType.Text);
            var pHidden = ins.Parameters.Add("$hidden", SqliteType.Integer);
            var pIcon = ins.Parameters.Add("$icon", SqliteType.Text);
            var pGray = ins.Parameters.Add("$gray", SqliteType.Text);
            var pPct = ins.Parameters.Add("$pct", SqliteType.Real);
            var pOrder = ins.Parameters.Add("$order", SqliteType.Integer);
            foreach (var d in catalog.Definitions)
            {
                pGame.Value = gameId;
                pApi.Value = d.ApiName;
                pName.Value = (object?)d.DisplayName ?? DBNull.Value;
                pDesc.Value = (object?)d.Description ?? DBNull.Value;
                pHidden.Value = d.Hidden ? 1 : 0;
                pIcon.Value = (object?)d.IconPath ?? DBNull.Value;
                pGray.Value = (object?)d.IconGrayPath ?? DBNull.Value;
                pPct.Value = d.GlobalPercent is { } pct ? pct : DBNull.Value;
                pOrder.Value = d.SortOrder;
                ins.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public AchievementCatalog? LoadCatalog(long gameId)
    {
        using var connection = database.Open();
        string source;
        bool complete;
        string? notes;
        DateTimeOffset retrieved;
        using (var meta = connection.CreateCommand())
        {
            meta.CommandText = "SELECT source, is_complete, notes, retrieved_at FROM catalogs WHERE game_id=$game;";
            meta.Parameters.AddWithValue("$game", gameId);
            using var r = meta.ExecuteReader();
            if (!r.Read())
            {
                return null;
            }

            source = r.GetString(0);
            complete = r.GetInt64(1) != 0;
            notes = r.IsDBNull(2) ? null : r.GetString(2);
            retrieved = AchicrackDatabase.FromUnixMs(r.GetInt64(3));
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT api_name, display_name, description, hidden, icon_path, icon_gray_path, global_percent, sort_order
            FROM achievement_definitions WHERE game_id=$game ORDER BY sort_order, api_name;
            """;
        cmd.Parameters.AddWithValue("$game", gameId);
        using var reader = cmd.ExecuteReader();
        var defs = new List<AchievementDefinition>();
        while (reader.Read())
        {
            defs.Add(new AchievementDefinition
            {
                ApiName = reader.GetString(0),
                DisplayName = reader.IsDBNull(1) ? null : reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                Hidden = reader.GetInt64(3) != 0,
                IconPath = reader.IsDBNull(4) ? null : reader.GetString(4),
                IconGrayPath = reader.IsDBNull(5) ? null : reader.GetString(5),
                GlobalPercent = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                SortOrder = (int)reader.GetInt64(7),
            });
        }

        return new AchievementCatalog
        {
            Definitions = defs,
            IsComplete = complete,
            Source = source,
            Notes = notes,
            RetrievedAtUtc = retrieved,
        };
    }

    public IReadOnlyList<GameAchievementStats> GetStats()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT g.id,
                   (SELECT COUNT(*) FROM unlocks u WHERE u.game_id = g.id),
                   (SELECT COUNT(*) FROM achievement_definitions d WHERE d.game_id = g.id),
                   COALESCE((SELECT is_complete FROM catalogs c WHERE c.game_id = g.id), 0)
            FROM games g;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<GameAchievementStats>();
        while (reader.Read())
        {
            result.Add(new GameAchievementStats(reader.GetInt64(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2),
                reader.GetInt64(3) != 0));
        }

        return result;
    }

    private static UnlockRecord ReadUnlock(SqliteDataReader r, int o) => new()
    {
        GameId = r.GetInt64(o),
        ApiName = r.GetString(o + 1),
        UnlockedAtUtc = AchicrackDatabase.FromUnixMs(r.GetInt64(o + 2)),
        UnlockTimeFromSource = r.GetInt64(o + 3) != 0,
        ObservedAtUtc = AchicrackDatabase.FromUnixMs(r.GetInt64(o + 4)),
        Source = r.GetString(o + 5),
        IsBaseline = r.GetInt64(o + 6) != 0,
    };
}
