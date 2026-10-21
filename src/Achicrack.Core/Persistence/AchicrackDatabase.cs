using Microsoft.Data.Sqlite;

namespace Achicrack.Core.Persistence;

/// <summary>
/// Owns the SQLite file: connection creation and schema migrations.
/// WAL journaling keeps the database consistent across unexpected termination.
/// </summary>
public sealed class AchicrackDatabase
{
    private static readonly string[] Migrations =
    [
        // v1 — initial schema
        """
        CREATE TABLE games (
            id                  INTEGER PRIMARY KEY AUTOINCREMENT,
            name                TEXT    NOT NULL,
            install_directory   TEXT    NOT NULL,
            app_id              INTEGER NULL,
            app_id_source       TEXT    NULL,
            adapter_id          TEXT    NULL,
            steam_api_directory TEXT    NULL,
            executables         TEXT    NOT NULL DEFAULT '[]',
            icon_path           TEXT    NULL,
            enabled             INTEGER NOT NULL DEFAULT 1,
            added_at            INTEGER NOT NULL,
            last_played_at      INTEGER NULL
        );

        CREATE TABLE unlocks (
            game_id             INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            api_name            TEXT    NOT NULL,
            unlocked_at         INTEGER NOT NULL,
            unlock_time_source  INTEGER NOT NULL DEFAULT 0,
            observed_at         INTEGER NOT NULL,
            source              TEXT    NOT NULL,
            is_baseline         INTEGER NOT NULL DEFAULT 0,
            notified_at         INTEGER NULL,
            PRIMARY KEY (game_id, api_name)
        );
        CREATE INDEX ix_unlocks_unlocked_at ON unlocks(unlocked_at DESC);

        CREATE TABLE catalogs (
            game_id             INTEGER PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
            source              TEXT    NOT NULL,
            is_complete         INTEGER NOT NULL,
            notes               TEXT    NULL,
            retrieved_at        INTEGER NOT NULL
        );

        CREATE TABLE achievement_definitions (
            game_id             INTEGER NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            api_name            TEXT    NOT NULL,
            display_name        TEXT    NULL,
            description         TEXT    NULL,
            hidden              INTEGER NOT NULL DEFAULT 0,
            icon_path           TEXT    NULL,
            icon_gray_path      TEXT    NULL,
            global_percent      REAL    NULL,
            sort_order          INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (game_id, api_name)
        );
        """,
    ];

    public AchicrackDatabase(string databasePath)
    {
        DatabasePath = Path.GetFullPath(databasePath);
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 10,
        }.ToString();
    }

    public string DatabasePath { get; }

    public string ConnectionString { get; }

    public int SchemaVersion => Migrations.Length;

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "PRAGMA synchronous=NORMAL;");

        var version = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        if (version > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"Database schema v{version} is newer than this build supports (v{Migrations.Length}).");
        }

        for (var i = version; i < Migrations.Length; i++)
        {
            using var tx = connection.BeginTransaction();
            Execute(connection, Migrations[i], tx);
            Execute(connection, $"PRAGMA user_version = {i + 1};", tx);
            tx.Commit();
        }
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    internal static long ToUnixMs(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromUnixMs(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
