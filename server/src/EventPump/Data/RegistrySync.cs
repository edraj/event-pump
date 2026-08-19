using EventPump.Config;
using Npgsql;

namespace EventPump.Data;

/// <summary>
/// Projects each tenant's tracking plan into event_registry at process boot
/// so emit_event() (SQL producer path) enforces the same allowlist + routing
/// map as the HTTP endpoints (SPEC §13). Reserved names (SPEC §6.1) sync
/// their `reserved` flag; emit_event() rejects reserved names with
/// `reserved_event_name` so producers cannot emit them directly.
/// </summary>
public static class RegistrySync
{
    /// <summary>Syncs every tenant in the registry.</summary>
    public static async Task SyncAllAsync(
        NpgsqlDataSource dataSource, TenantRegistry tenants, CancellationToken ct = default)
    {
        foreach (var tenant in tenants.All)
            await SyncTenantAsync(dataSource, tenant.AppId, tenant.Plan, ct);
    }

    /// <summary>Syncs one tenant's plan. Idempotent; safe to call at every boot.</summary>
    public static async Task SyncTenantAsync(
        NpgsqlDataSource dataSource, string appId, TrackingPlan plan, CancellationToken ct = default)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // first_visit must exist for every tenant so emit_event's internal
        // emission can never fail a producer's business transaction. Routing
        // defaults to nothing — the tenant's plan overrides it via the upsert
        // below if it declares first_visit explicitly.
        await using (var seed = new NpgsqlCommand(
            """
            INSERT INTO event_registry (app_id, event_name, origin, destinations)
            VALUES ($1, 'first_visit', 'server', '{}')
            ON CONFLICT (app_id, event_name) DO NOTHING
            """, conn, tx))
        {
            seed.Parameters.Add(new() { Value = appId });
            await seed.ExecuteNonQueryAsync(ct);
        }

        foreach (var (name, evt) in plan.Events)
        {
            await using var upsert = new NpgsqlCommand(
                """
                INSERT INTO event_registry (app_id, event_name, origin, destinations, adjust_token, reserved)
                VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT (app_id, event_name) DO UPDATE SET
                    origin       = EXCLUDED.origin,
                    destinations = EXCLUDED.destinations,
                    adjust_token = EXCLUDED.adjust_token,
                    reserved     = EXCLUDED.reserved,
                    updated_at   = now()
                """, conn, tx);
            upsert.Parameters.Add(new() { Value = appId });
            upsert.Parameters.Add(new() { Value = name });
            upsert.Parameters.Add(new() { Value = evt.Origin });
            upsert.Parameters.Add(new() { Value = evt.Destinations });
            upsert.Parameters.Add(new() { Value = (object?)evt.AdjustToken ?? DBNull.Value });
            upsert.Parameters.Add(new() { Value = evt.Reserved });
            await upsert.ExecuteNonQueryAsync(ct);
        }

        // Names dropped from the tenant's plan disappear from that tenant's
        // allowlist; the seeded first_visit safety row always survives. Other
        // tenants' rows are untouched because the DELETE is scoped by app_id.
        await using var prune = new NpgsqlCommand(
            """
            DELETE FROM event_registry
             WHERE app_id = $1
               AND event_name <> 'first_visit'
               AND event_name <> ALL($2)
            """, conn, tx);
        prune.Parameters.Add(new() { Value = appId });
        prune.Parameters.Add(new() { Value = plan.Events.Keys.ToArray() });
        await prune.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
    }
}
