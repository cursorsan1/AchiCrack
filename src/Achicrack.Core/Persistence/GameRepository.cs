using System.Text.Json;
using Achicrack.Core.Models;
using Microsoft.Data.Sqlite;

namespace Achicrack.Core.Persistence;

public sealed class GameRepository(AchicrackDatabase database)
{
    private const string SelectColumns =
        "id, name, install_directory, app_id, app_id_source, adapter_id, steam_api_directory, executables, icon_path, enabled, added_at, last_played_at";

    public long Add(GameRecord game)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO games (name, install_directory, app_id, app_id_source, adapter_id, steam_api_directory,
                               executables, icon_path, enabled, added_at, last_played_at)
            VALUES ($name, $dir, $appId, $appIdSource, $adapter, $apiDir, $exes, $icon, $enabled, $added, $played);
            SELECT last_insert_rowid();
            """;
        Bind(cmd, game);
        game.Id = Convert.ToInt64(cmd.ExecuteScalar());
        return game.Id;
    }

    public void Update(GameRecord game)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE games SET name=$name, install_directory=$dir, app_id=$appId, app_id_source=$appIdSource,
                adapter_id=$adapter, steam_api_directory=$apiDir, executables=$exes, icon_path=$icon,
                enabled=$enabled, added_at=$added, last_played_at=$played
            WHERE id=$id;
            """;
        Bind(cmd, game);
        cmd.Parameters.AddWithValue("$id", game.Id);
        cmd.ExecuteNonQuery();
    }

    public void SetLastPlayed(long gameId, DateTimeOffset when)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE games SET last_played_at=$played WHERE id=$id;";
        cmd.Parameters.AddWithValue("$played", AchicrackDatabase.ToUnixMs(when));
        cmd.Parameters.AddWithValue("$id", gameId);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long gameId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM games WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", gameId);
        cmd.ExecuteNonQuery();
    }

    public GameRecord? Get(long gameId)
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM games WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", gameId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public IReadOnlyList<GameRecord> GetAll()
    {
        using var connection = database.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectColumns} FROM games ORDER BY name COLLATE NOCASE;";
        using var reader = cmd.ExecuteReader();
        var result = new List<GameRecord>();
        while (reader.Read())
        {
            result.Add(Read(reader));
        }

        return result;
    }

    private static void Bind(SqliteCommand cmd, GameRecord game)
    {
        cmd.Parameters.AddWithValue("$name", game.Name);
        cmd.Parameters.AddWithValue("$dir", game.InstallDirectory);
        cmd.Parameters.AddWithValue("$appId", game.AppId is { } a ? (long)a : DBNull.Value);
        cmd.Parameters.AddWithValue("$appIdSource", (object?)game.AppIdSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adapter", (object?)game.AdapterId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$apiDir", (object?)game.SteamApiDirectory ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exes", JsonSerializer.Serialize(game.Executables));
        cmd.Parameters.AddWithValue("$icon", (object?)game.IconPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", game.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$added", AchicrackDatabase.ToUnixMs(game.AddedAtUtc));
        cmd.Parameters.AddWithValue("$played",
            game.LastPlayedAtUtc is { } p ? AchicrackDatabase.ToUnixMs(p) : DBNull.Value);
    }

    private static GameRecord Read(SqliteDataReader r)
    {
        List<string> exes;
        try
        {
            exes = JsonSerializer.Deserialize<List<string>>(r.GetString(7)) ?? [];
        }
        catch (JsonException)
        {
            exes = [];
        }

        return new GameRecord
        {
            Id = r.GetInt64(0),
            Name = r.GetString(1),
            InstallDirectory = r.GetString(2),
            AppId = r.IsDBNull(3) ? null : (uint)r.GetInt64(3),
            AppIdSource = r.IsDBNull(4) ? null : r.GetString(4),
            AdapterId = r.IsDBNull(5) ? null : r.GetString(5),
            SteamApiDirectory = r.IsDBNull(6) ? null : r.GetString(6),
            Executables = exes,
            IconPath = r.IsDBNull(8) ? null : r.GetString(8),
            Enabled = r.GetInt64(9) != 0,
            AddedAtUtc = AchicrackDatabase.FromUnixMs(r.GetInt64(10)),
            LastPlayedAtUtc = r.IsDBNull(11) ? null : AchicrackDatabase.FromUnixMs(r.GetInt64(11)),
        };
    }
}
