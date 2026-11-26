using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ReeveWorkflow.Editor.Tests;

public sealed class SqliteLockShapeTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"t19-lock-{Guid.NewGuid():N}.db");

    public SqliteLockShapeTests()
    {
        using var setup = Open();
        Run(setup, "CREATE TABLE workflows (id INTEGER PRIMARY KEY, revision INTEGER NOT NULL); INSERT INTO workflows VALUES (1, 0)");
    }

    [Fact]
    public async Task P7_efs_transaction_holds_the_write_lock_from_its_start()
    {
        await using var door = Open();
        await using var context = new DbContext(new DbContextOptionsBuilder().UseSqlite(door).Options);
        await using var transaction = await context.Database.BeginTransactionAsync();
        await using var peer = Open();

        var write = () => Run(peer, "UPDATE workflows SET revision = revision + 1 WHERE id = 1");

        write.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(5, "SQLITE_BUSY: database is locked");
    }

    [Fact]
    public void P7a_select_for_update_is_a_syntax_error()
    {
        using var connection = Open();

        var read = () => Run(connection, "SELECT revision FROM workflows WHERE id = 1 FOR UPDATE");

        read.Should().Throw<SqliteException>().WithMessage("*near \"FOR\": syntax error*");
    }

    [Fact]
    public void P7d_a_deferred_transaction_lets_a_peer_land_and_then_cannot_write()
    {
        using var door = Open();
        Run(door, "PRAGMA journal_mode = WAL");
        Run(door, "BEGIN DEFERRED");
        Run(door, "SELECT revision FROM workflows WHERE id = 1");
        using var peer = Open();

        Run(peer, "UPDATE workflows SET revision = revision + 1 WHERE id = 1");
        var promote = () => Run(door, "UPDATE workflows SET revision = revision + 1 WHERE id = 1 AND revision = 0");

        promote.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(5, "SQLITE_BUSY: database is locked");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            File.Delete(file);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={path};Default Timeout=1;Pooling=False");
        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
