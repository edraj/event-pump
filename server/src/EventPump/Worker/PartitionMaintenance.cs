using System.Globalization;
using Npgsql;

namespace EventPump.Worker;

/// <summary>
/// Partition + retention maintenance (SPEC §11): pre-creates day partitions
/// ahead, drops outbox/delivery pairs past retention — a day containing 'dead'
/// delivery rows is held until retentionDeadDays to preserve the failure
/// evidence — and prunes events_dedupe. Runs on a worker-hosted timer.
/// </summary>
public static class PartitionMaintenance
{
    public static async Task RunOnceAsync(
        NpgsqlDataSource dataSource,
        int retentionDays,
        int retentionDeadDays,
        int aheadDays,
        CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        for (var offset = -1; offset <= aheadDays; offset++)
            await Exec(conn, $"SELECT ep_ensure_partitions(current_date + {offset})", ct);

        DateOnly today;
        await using (var cmd = new NpgsqlCommand("SELECT current_date", conn))
        {
            today = (DateOnly)(await cmd.ExecuteScalarAsync(ct))!;
        }

        var partitions = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT c.relname
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            JOIN pg_class p ON p.oid = i.inhparent
            WHERE p.relname = 'events_outbox'
            """, conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct)) partitions.Add(reader.GetString(0));
        }

        var cutoff = today.AddDays(-retentionDays);
        var deadCutoff = today.AddDays(-retentionDeadDays);
        foreach (var relname in partitions)
        {
            var suffix = relname[(relname.LastIndexOf('_') + 1)..];
            if (!DateOnly.TryParseExact(suffix, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var day) || day >= cutoff)
                continue;

            var hasDead = false;
            await using (var cmd = new NpgsqlCommand(
                $"""
                SELECT to_regclass('events_delivery_{suffix}') IS NOT NULL
                       AND EXISTS (SELECT 1 FROM events_delivery_{suffix} WHERE status = 'dead')
                """, conn))
            {
                hasDead = (bool)(await cmd.ExecuteScalarAsync(ct))!;
            }
            if (hasDead && day >= deadCutoff) continue;

            await Exec(conn, $"DROP TABLE IF EXISTS events_delivery_{suffix}", ct);
            await Exec(conn, $"DROP TABLE IF EXISTS events_outbox_{suffix}", ct);
        }

        await Exec(conn,
            $"DELETE FROM events_dedupe WHERE received_at < now() - make_interval(days => {retentionDays})", ct);
    }

    private static async Task Exec(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
