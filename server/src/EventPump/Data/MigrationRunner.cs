using Npgsql;

namespace EventPump.Data;

/// <summary>
/// Tiny plain-.sql migration runner (SPEC/PLAN: no EF).
/// Applies migrations/*.sql in name order exactly once (recorded in
/// schema_migrations), then always re-applies the idempotent producer
/// contract (CREATE OR REPLACE), then pre-creates daily partitions so
/// ingestion works immediately after migrate.
/// </summary>
public static class MigrationRunner
{
    // Arbitrary constant; serializes concurrent migrators cluster-wide.
    private const long AdvisoryLockKey = 0x45505F6D69677261; // "EP_migra"

    public static async Task ApplyAsync(
        NpgsqlDataSource dataSource,
        string migrationsDir,
        string producerContractPath,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await Exec(conn, $"SELECT pg_advisory_lock({AdvisoryLockKey})", ct);
        try
        {
            await Exec(conn,
                """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    version    text PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT now())
                """, ct);

            var applied = new HashSet<string>();
            await using (var cmd = new NpgsqlCommand("SELECT version FROM schema_migrations", conn))
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));
            }

            var files = Directory.GetFiles(migrationsDir, "*.sql")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal);
            foreach (var file in files)
            {
                var version = Path.GetFileName(file);
                if (applied.Contains(version)) continue;
                log?.Invoke($"applying {version}");
                await using var tx = await conn.BeginTransactionAsync(ct);
                await Exec(conn, await File.ReadAllTextAsync(file, ct), ct, tx);
                await using (var mark = new NpgsqlCommand(
                    "INSERT INTO schema_migrations (version) VALUES ($1)", conn, tx))
                {
                    mark.Parameters.Add(new NpgsqlParameter { Value = version });
                    await mark.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }

            // Idempotent by construction (CREATE OR REPLACE); re-applied every
            // migrate so contract updates ship with normal deploys.
            log?.Invoke($"applying {Path.GetFileName(producerContractPath)}");
            await Exec(conn, await File.ReadAllTextAsync(producerContractPath, ct), ct);

            // Yesterday covers clock skew; the worker timer maintains the
            // forward window after this.
            for (var offset = -1; offset <= 3; offset++)
                await Exec(conn, $"SELECT ep_ensure_partitions(current_date + {offset})", ct);
        }
        finally
        {
            await Exec(conn, $"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task Exec(
        NpgsqlConnection conn, string sql, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
