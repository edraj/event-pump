using EventPump.Data;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Provides isolated PostgreSQL 18 test databases.
///
/// When EP_TEST_CONNSTRING is set (a connection string whose role has
/// CREATEDB), throwaway databases are created on that server and dropped on
/// dispose — no container runtime needed. Otherwise a postgres:18-alpine
/// Testcontainer is started. Credentials never live in this code.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string? ExternalConnString =
        Environment.GetEnvironmentVariable("EP_TEST_CONNSTRING");

    private readonly PostgreSqlContainer? _container =
        ExternalConnString is null ? new PostgreSqlBuilder("postgres:18-alpine").Build() : null;

    private readonly string _runId = Guid.NewGuid().ToString("N")[..8];
    private readonly List<(string Name, NpgsqlDataSource DataSource)> _databases = [];
    private int _dbCounter;

    private string AdminConnString => ExternalConnString ?? _container!.GetConnectionString();

    public Task InitializeAsync() => _container?.StartAsync() ?? Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (_, ds) in _databases) await ds.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync(); // databases die with the container
            return;
        }
        await using var admin = NpgsqlDataSource.Create(AdminConnString);
        foreach (var (name, _) in _databases)
        {
            await using var cmd = admin.CreateCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
            await cmd.ExecuteNonQueryAsync();
        }
    }

    /// <summary>Fresh, isolated database with all migrations + producer contract applied.</summary>
    public async Task<NpgsqlDataSource> CreateMigratedDatabaseAsync()
    {
        var ds = await CreateEmptyDatabaseAsync();
        await MigrationRunner.ApplyAsync(ds, RepoPaths.MigrationsDir, RepoPaths.ProducerContract);
        return ds;
    }

    /// <summary>Fresh, isolated, empty database.</summary>
    public async Task<NpgsqlDataSource> CreateEmptyDatabaseAsync()
    {
        var name = $"ep_test_{_runId}_{Interlocked.Increment(ref _dbCounter)}";
        // Cap the one-shot admin pool to 1 — each `NpgsqlDataSource.Create`
        // spins up its own pool that defaults to 100 conns, so N parallel
        // test classes each holding 100 idle admin conns easily blows past
        // Postgres's max_connections=100. We only need 1 for `CREATE DATABASE`.
        var adminCsb = new NpgsqlConnectionStringBuilder(AdminConnString) { MaxPoolSize = 1 };
        await using (var admin = NpgsqlDataSource.Create(adminCsb.ConnectionString))
        await using (var cmd = admin.CreateCommand($"CREATE DATABASE {name}"))
        {
            await cmd.ExecuteNonQueryAsync();
        }
        // Also cap the per-test-DB pool. Each test class opens its own DB and
        // its own pool; without a cap they collectively pin ~150 conns and
        // trip Postgres's default max_connections=100 under parallel xunit.
        var csb = new NpgsqlConnectionStringBuilder(AdminConnString)
        {
            Database = name,
            MaxPoolSize = 3,
        };
        var ds = NpgsqlDataSource.Create(csb.ConnectionString);
        lock (_databases) _databases.Add((name, ds));
        return ds;
    }
}

[CollectionDefinition("pg")]
public sealed class PgCollection : ICollectionFixture<PostgresFixture>;
