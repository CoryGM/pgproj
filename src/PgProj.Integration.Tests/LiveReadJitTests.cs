using Npgsql;
using PgProj.Core;
using PgProj.Parser;
using PgProj.Semantics;

namespace PgProj.Integration.Tests;

/// <summary>
/// G-328: the live database is read with <c>jit</c> off, and the session's own setting is put back afterwards. PostgreSQL 14 to 16 JIT-compiled the
/// aggregates' catalog query on every read (about 0.8 s of a 1.5 s read), which made the integration suite six times slower on them than on 17 and 18.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class LiveReadJitTests
{
    private const double Bound = 3;

    private readonly LiveDatabaseModelLoader _liveLoader = new(new PgQueryParser(), new SemanticBuilder());

    [TestMethod]
    [TestCategory("Performance")]
    public async Task AReadTakesNoLongerWhenTheSessionJitCompilesEveryQuery()
    {
        // jit_above_cost 0 makes the server JIT-compile, inline and optimize every query the session runs; a read that leaves jit on pays for each of its
        // catalog queries. Before G-328 a read took many times as long so.
        await using var plain = await OpenConnectionAsync();
        if (!await ScalarAsync<bool>(plain, "select pg_catalog.pg_jit_available()"))
        {
            Assert.Inconclusive("The server has no JIT provider.");
        }

        await ExecuteAsync(plain, "set jit = off");
        await using var compiling = await OpenConnectionAsync();
        await ExecuteAsync(compiling, "set jit = on; set jit_above_cost = 0; set jit_inline_above_cost = 0; set jit_optimize_above_cost = 0");

        var withoutJit = await TimedRuns.FastestAsync(() => _liveLoader.LoadAsync(plain, CancellationToken.None));
        var withJit = await TimedRuns.FastestAsync(() => _liveLoader.LoadAsync(compiling, CancellationToken.None));

        var ratio = withJit / withoutJit;
        Assert.IsLessThan(Bound, ratio, $"A read in a session that JIT-compiles every query took {ratio:F1}x the time");
    }

    [TestMethod]
    public async Task EveryReadPutsBackTheSessionsJitSetting()
    {
        await using var connection = await OpenConnectionAsync();
        await ExecuteAsync(connection, "set jit = on");

        await _liveLoader.LoadAsync(connection, CancellationToken.None);
        Assert.AreEqual("on", await ScalarAsync<string>(connection, "select pg_catalog.current_setting('jit')"), "LoadAsync");

        await _liveLoader.LoadForScaffoldAsync(connection, CancellationToken.None);
        Assert.AreEqual("on", await ScalarAsync<string>(connection, "select pg_catalog.current_setting('jit')"), "LoadForScaffoldAsync");

        await _liveLoader.ReadLikeSourcesAsync(connection, ["public.no_such_table"], CancellationToken.None);
        Assert.AreEqual("on", await ScalarAsync<string>(connection, "select pg_catalog.current_setting('jit')"), "ReadLikeSourcesAsync");

        await ExecuteAsync(connection, "set jit = off");
        await _liveLoader.LoadAsync(connection, CancellationToken.None);
        Assert.AreEqual("off", await ScalarAsync<string>(connection, "select pg_catalog.current_setting('jit')"), "LoadAsync, jit off");
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(PostgresContainerFixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
