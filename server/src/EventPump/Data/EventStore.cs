using EventPump.Api;
using Npgsql;
using NpgsqlTypes;

namespace EventPump.Data;

/// <summary>Storage for HTTP-ingested events and identity upserts (SPEC §9, §11).</summary>
public static class EventStore
{
    // One round trip for a whole batch: dedupe -> outbox -> routed fan-out.
    // Duplicate event_ids drop out at the dedup CTE (idempotent accept, SPEC §1).
    private const string InsertBatchSql =
        """
        WITH input AS (
            SELECT * FROM unnest(
                $1::uuid[], $2::text[], $3::timestamptz[],
                $4::uuid[], $5::uuid[], $6::text[], $7::jsonb[], $8::jsonb[])
            AS t(event_id, event_name, occurred_at, anonymous_id, session_key,
                 user_id, properties, context)
        ), dedup AS (
            INSERT INTO events_dedupe (event_id)
            SELECT event_id FROM input
            ON CONFLICT DO NOTHING
            RETURNING event_id
        ), outbox AS (
            INSERT INTO events_outbox
                (event_id, event_name, origin, occurred_at, received_at,
                 user_id, anonymous_id, session_key, properties, context)
            SELECT i.event_id, i.event_name, $9, i.occurred_at, now(),
                   i.user_id, i.anonymous_id, i.session_key, i.properties, i.context
            FROM input i
            JOIN dedup d USING (event_id)
            RETURNING id, received_at, event_name
        )
        INSERT INTO events_delivery (event_ref, received_at, destination)
        SELECT o.id, o.received_at, dest.d
        FROM outbox o
        JOIN event_registry r ON r.event_name = o.event_name
        CROSS JOIN LATERAL unnest(r.destinations) AS dest(d)
        """;

    public static async Task InsertBatchAsync(
        NpgsqlDataSource dataSource, string origin, IReadOnlyList<ParsedEvent> events, CancellationToken ct)
    {
        if (events.Count == 0) return;

        var count = events.Count;
        var eventIds = new Guid[count];
        var names = new string[count];
        var occurredAts = new DateTime[count];
        var anonymousIds = new string?[count];
        var sessionKeys = new string?[count];
        var userIds = new string?[count];
        var properties = new string[count];
        var contexts = new string[count];
        for (var i = 0; i < count; i++)
        {
            var e = events[i];
            eventIds[i] = e.EventId;
            names[i] = e.EventName;
            occurredAts[i] = e.OccurredAt.UtcDateTime;
            anonymousIds[i] = e.AnonymousId?.ToString();
            sessionKeys[i] = e.SessionKey?.ToString();
            userIds[i] = e.UserId;
            properties[i] = e.PropertiesJson;
            contexts[i] = e.ContextJson;
        }

        await using var cmd = dataSource.CreateCommand(InsertBatchSql);
        cmd.Parameters.Add(new() { Value = eventIds });
        cmd.Parameters.Add(new() { Value = names });
        cmd.Parameters.Add(new() { Value = occurredAts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = anonymousIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = sessionKeys, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = userIds, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = properties, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = contexts, NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = origin });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record IdentityUpsert(
        Guid SessionKey,
        Guid AnonymousId,
        int? SessionNumber,
        string? UserId,
        string? Ga4ClientId,
        string? Ga4SessionId,
        string? FirebaseAppInstanceId,
        string? AmplitudeDeviceId,
        string? AdjustAdid,
        string? AdjustPlatformAdId,
        string? Fbp,
        string? Fbc,
        string? ClickIdsJson,
        string? ContextJson,
        string? Email,
        string? Msisdn);

    /// <summary>
    /// Partial upsert (SPEC §9.2): present fields overwrite, absent fields survive;
    /// click_ids and context merge at the top level. Runs the first-visit gate and
    /// emits `first_visit` (via emit_event) on the once-ever insert. Returns true
    /// when this call was the first visit.
    /// </summary>
    public static async Task<bool> UpsertIdentityAsync(
        NpgsqlDataSource dataSource, IdentityUpsert identity, string? clientIp, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO identity_registry (
                session_key, anonymous_id, user_id, session_number,
                ga4_client_id, ga4_session_id, firebase_app_instance_id,
                amplitude_device_id, adjust_adid, adjust_platform_ad_id,
                fbp, fbc, click_ids, context, client_ip, email, msisdn)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12,
                    coalesce($13::jsonb, '{}'), coalesce($14::jsonb, '{}'), $15, $16, $17)
            ON CONFLICT (session_key) DO UPDATE SET
                anonymous_id             = EXCLUDED.anonymous_id,
                user_id                  = coalesce(EXCLUDED.user_id, identity_registry.user_id),
                session_number           = coalesce(EXCLUDED.session_number, identity_registry.session_number),
                ga4_client_id            = coalesce(EXCLUDED.ga4_client_id, identity_registry.ga4_client_id),
                ga4_session_id           = coalesce(EXCLUDED.ga4_session_id, identity_registry.ga4_session_id),
                firebase_app_instance_id = coalesce(EXCLUDED.firebase_app_instance_id, identity_registry.firebase_app_instance_id),
                amplitude_device_id      = coalesce(EXCLUDED.amplitude_device_id, identity_registry.amplitude_device_id),
                adjust_adid              = coalesce(EXCLUDED.adjust_adid, identity_registry.adjust_adid),
                adjust_platform_ad_id    = coalesce(EXCLUDED.adjust_platform_ad_id, identity_registry.adjust_platform_ad_id),
                fbp                      = coalesce(EXCLUDED.fbp, identity_registry.fbp),
                fbc                      = coalesce(EXCLUDED.fbc, identity_registry.fbc),
                click_ids                = identity_registry.click_ids || EXCLUDED.click_ids,
                context                  = identity_registry.context || EXCLUDED.context,
                client_ip                = coalesce(EXCLUDED.client_ip, identity_registry.client_ip),
                email                    = coalesce(EXCLUDED.email, identity_registry.email),
                msisdn                   = coalesce(EXCLUDED.msisdn, identity_registry.msisdn),
                updated_at               = now()
            """, conn, tx))
        {
            upsert.Parameters.Add(new() { Value = identity.SessionKey });
            upsert.Parameters.Add(new() { Value = identity.AnonymousId });
            upsert.Parameters.Add(Nullable(identity.UserId));
            upsert.Parameters.Add(new() { Value = (object?)identity.SessionNumber ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
            upsert.Parameters.Add(Nullable(identity.Ga4ClientId));
            upsert.Parameters.Add(Nullable(identity.Ga4SessionId));
            upsert.Parameters.Add(Nullable(identity.FirebaseAppInstanceId));
            upsert.Parameters.Add(Nullable(identity.AmplitudeDeviceId));
            upsert.Parameters.Add(Nullable(identity.AdjustAdid));
            upsert.Parameters.Add(Nullable(identity.AdjustPlatformAdId));
            upsert.Parameters.Add(Nullable(identity.Fbp));
            upsert.Parameters.Add(Nullable(identity.Fbc));
            upsert.Parameters.Add(Nullable(identity.ClickIdsJson));
            upsert.Parameters.Add(Nullable(identity.ContextJson));
            upsert.Parameters.Add(Nullable(clientIp));
            upsert.Parameters.Add(Nullable(identity.Email));
            upsert.Parameters.Add(Nullable(identity.Msisdn));
            await upsert.ExecuteNonQueryAsync(ct);
        }

        bool firstVisit;
        await using (var gate = new NpgsqlCommand(
            "INSERT INTO first_seen (anonymous_id) VALUES ($1) ON CONFLICT DO NOTHING RETURNING anonymous_id",
            conn, tx))
        {
            gate.Parameters.Add(new() { Value = identity.AnonymousId });
            firstVisit = await gate.ExecuteScalarAsync(ct) is not null;
        }

        if (firstVisit)
        {
            await using var emit = new NpgsqlCommand(
                """
                SELECT emit_event('first_visit',
                                  p_user_id      => $1,
                                  p_anonymous_id => $2,
                                  p_session_key  => $3)
                """, conn, tx);
            emit.Parameters.Add(Nullable(identity.UserId));
            emit.Parameters.Add(new() { Value = identity.AnonymousId });
            emit.Parameters.Add(new() { Value = identity.SessionKey });
            await emit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return firstVisit;
    }

    private static NpgsqlParameter Nullable(string? value)
        => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
