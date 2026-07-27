using Npgsql;
using NpgsqlTypes;

namespace EventPump.Tests;

/// <summary>Minimal SQL helpers for tests. Default tenant is "zainmart".</summary>
internal static class Db
{
    public const string DefaultAppId = "zainmart";

    public static async Task Exec(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<T> Scalar<T>(NpgsqlDataSource ds, string sql)
    {
        await using var cmd = ds.CreateCommand(sql);
        var result = await cmd.ExecuteScalarAsync();
        return (T)result!;
    }

    public static Task RegisterEvent(
        NpgsqlDataSource ds, string name, string origin, params string[] destinations)
        => RegisterEvent(ds, DefaultAppId, name, origin, destinations);

    public static async Task RegisterEvent(
        NpgsqlDataSource ds, string appId, string name, string origin, params string[] destinations)
    {
        await using var cmd = ds.CreateCommand(
            """
            INSERT INTO event_registry (app_id, event_name, origin, destinations)
            VALUES ($1, $2, $3, $4)
            ON CONFLICT (app_id, event_name)
            DO UPDATE SET origin = EXCLUDED.origin, destinations = EXCLUDED.destinations
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = origin });
        cmd.Parameters.Add(new() { Value = destinations });
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Calls emit_event(); returns the event_id or null on duplicate no-op.</summary>
    public static Task<Guid?> Emit(
        NpgsqlDataSource ds, string name, Guid? eventId = null, Guid? anonymousId = null)
        => Emit(ds, DefaultAppId, name, eventId, anonymousId);

    public static async Task<Guid?> Emit(
        NpgsqlDataSource ds, string appId, string name, Guid? eventId = null, Guid? anonymousId = null)
    {
        await using var cmd = ds.CreateCommand(
            """
            SELECT emit_event(
                p_app_id       => $1,
                p_event_name   => $2,
                p_event_id     => coalesce($3, gen_random_uuid()),
                p_anonymous_id => $4)
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)eventId ?? DBNull.Value });
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)anonymousId ?? DBNull.Value });
        var result = await cmd.ExecuteScalarAsync();
        return result is Guid g ? g : null;
    }

    /// <summary>Destinations fanned out for a given event_id, joined outbox→delivery.</summary>
    public static async Task<string[]> DeliveryDestinations(NpgsqlDataSource ds, Guid eventId)
    {
        await using var cmd = ds.CreateCommand(
            """
            SELECT coalesce(array_agg(d.destination ORDER BY d.destination), '{}')
            FROM events_delivery d
            JOIN events_outbox o ON o.id = d.event_ref AND o.received_at = d.received_at
            WHERE o.event_id = $1
            """);
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = eventId });
        return (string[])(await cmd.ExecuteScalarAsync())!;
    }
}
