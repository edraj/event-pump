using EventPump.Config;
using Npgsql;

namespace EventPump.Data;

/// <summary>
/// Projects the tracking plan into the event_registry table at process boot so
/// emit_event() (SQL producer path) enforces the same allowlist + routing map
/// as the HTTP endpoints (SPEC §13).
/// </summary>
public static class RegistrySync
{
    public static async Task SyncAsync(NpgsqlDataSource dataSource, TrackingPlan plan, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        foreach (var (name, evt) in plan.Events)
        {
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO event_registry (event_name, origin, destinations, meta_name, adjust_token)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (event_name) DO UPDATE SET
                    origin       = EXCLUDED.origin,
                    destinations = EXCLUDED.destinations,
                    meta_name    = EXCLUDED.meta_name,
                    adjust_token = EXCLUDED.adjust_token,
                    updated_at   = now()
                """, conn, tx);
            upsert.Parameters.Add(new() { Value = name });
            upsert.Parameters.Add(new() { Value = evt.Origin });
            upsert.Parameters.Add(new() { Value = evt.Destinations });
            upsert.Parameters.Add(new() { Value = (object?)evt.MetaName ?? DBNull.Value });
            upsert.Parameters.Add(new() { Value = (object?)evt.AdjustToken ?? DBNull.Value });
            await upsert.ExecuteNonQueryAsync(ct);
        }

        // Names dropped from the plan disappear from the allowlist; the seeded
        // first_visit safety row always survives (see 0004_event_registry.sql).
        await using var prune = new NpgsqlCommand(
            "DELETE FROM event_registry WHERE event_name <> 'first_visit' AND event_name <> ALL($1)",
            conn, tx);
        prune.Parameters.Add(new() { Value = plan.Events.Keys.ToArray() });
        await prune.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }
}
