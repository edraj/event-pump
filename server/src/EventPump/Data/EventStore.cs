using EventPump.Api;
using Npgsql;
using NpgsqlTypes;

namespace EventPump.Data;

/// <summary>
/// Storage for HTTP-ingested events and identity upserts (SPEC §9, §11).
/// Every method takes an app_id so a bug in one tenant's handler cannot
/// spill into another tenant's rows.
/// </summary>
public static class EventStore
{
    // One round trip for a whole batch: dedupe -> outbox -> routed fan-out,
    // all keyed to the same app_id ($10). The event_registry lookup is
    // narrowed by app_id so a tenant that has not registered the event name
    // gets zero delivery rows even if another tenant has the same name
    // routed elsewhere.
    private const string InsertBatchSql =
        """
        WITH input AS (
            SELECT * FROM unnest(
                $1::uuid[], $2::text[], $3::timestamptz[],
                $4::uuid[], $5::uuid[], $6::text[], $7::jsonb[], $8::jsonb[])
            AS t(event_id, event_name, occurred_at, anonymous_id, session_key,
                 user_id, properties, context)
        ), dedup AS (
            INSERT INTO events_dedupe (event_id, app_id)
            SELECT event_id, $10 FROM input
            ON CONFLICT DO NOTHING
            RETURNING event_id
        ), outbox AS (
            INSERT INTO events_outbox
                (app_id, event_id, event_name, origin, occurred_at, received_at,
                 user_id, anonymous_id, session_key, properties, context)
            SELECT $10, i.event_id, i.event_name, $9, i.occurred_at, now(),
                   i.user_id, i.anonymous_id, i.session_key, i.properties, i.context
            FROM input i
            JOIN dedup d USING (event_id)
            RETURNING id, received_at, event_name
        )
        INSERT INTO events_delivery (event_ref, received_at, app_id, destination)
        SELECT o.id, o.received_at, $10, dest.d
        FROM outbox o
        JOIN event_registry r ON r.app_id = $10 AND r.event_name = o.event_name
        CROSS JOIN LATERAL unnest(r.destinations) AS dest(d)
        """;

    public static async Task InsertBatchAsync(
        NpgsqlDataSource dataSource, string appId, string origin,
        IReadOnlyList<ParsedEvent> events, CancellationToken ct)
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
        cmd.Parameters.Add(new() { Value = appId });
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
        // Per-destination user identifiers (migration 0010, SPEC follow-up).
        // Each falls back to UserId in the sender when unset.
        string? MoEngageCustomerId = null,
        string? Ga4UserId = null,
        string? AmplitudeUserId = null,
        string? MetaExternalId = null);

    /// <summary>
    /// Partial upsert (SPEC §9.2): present fields overwrite, absent fields survive;
    /// click_ids and context merge at the top level. Runs the first-visit gate,
    /// keyed on (app_id, anonymous_id), and emits `first_visit` (via emit_event)
    /// on the once-ever insert. Returns true when this call was the first visit.
    /// </summary>
    public static async Task<bool> UpsertIdentityAsync(
        NpgsqlDataSource dataSource, string appId, IdentityUpsert identity,
        string? clientIp, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO identity_registry (
                app_id, session_key, anonymous_id, user_id, session_number,
                ga4_client_id, ga4_session_id, firebase_app_instance_id,
                amplitude_device_id, adjust_adid, adjust_platform_ad_id,
                fbp, fbc, click_ids, context, client_ip,
                moengage_customer_id, ga4_user_id, amplitude_user_id, meta_external_id)
            VALUES ($16, $1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12,
                    coalesce($13::jsonb, '{}'), coalesce($14::jsonb, '{}'), $15,
                    $17, $18, $19, $20)
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
                moengage_customer_id     = coalesce(EXCLUDED.moengage_customer_id, identity_registry.moengage_customer_id),
                ga4_user_id              = coalesce(EXCLUDED.ga4_user_id, identity_registry.ga4_user_id),
                amplitude_user_id        = coalesce(EXCLUDED.amplitude_user_id, identity_registry.amplitude_user_id),
                meta_external_id         = coalesce(EXCLUDED.meta_external_id, identity_registry.meta_external_id),
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
            upsert.Parameters.Add(new() { Value = appId });
            upsert.Parameters.Add(Nullable(identity.MoEngageCustomerId));
            upsert.Parameters.Add(Nullable(identity.Ga4UserId));
            upsert.Parameters.Add(Nullable(identity.AmplitudeUserId));
            upsert.Parameters.Add(Nullable(identity.MetaExternalId));
            await upsert.ExecuteNonQueryAsync(ct);
        }

        bool firstVisit;
        await using (var gate = new NpgsqlCommand(
            "INSERT INTO first_seen (app_id, anonymous_id) VALUES ($1, $2) ON CONFLICT DO NOTHING RETURNING anonymous_id",
            conn, tx))
        {
            gate.Parameters.Add(new() { Value = appId });
            gate.Parameters.Add(new() { Value = identity.AnonymousId });
            firstVisit = await gate.ExecuteScalarAsync(ct) is not null;
        }

        if (firstVisit)
        {
            await using var emit = new NpgsqlCommand(
                """
                SELECT emit_event($1, 'first_visit',
                                  p_user_id      => $2,
                                  p_anonymous_id => $3,
                                  p_session_key  => $4)
                """, conn, tx);
            emit.Parameters.Add(new() { Value = appId });
            emit.Parameters.Add(Nullable(identity.UserId));
            emit.Parameters.Add(new() { Value = identity.AnonymousId });
            emit.Parameters.Add(new() { Value = identity.SessionKey });
            await emit.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return firstVisit;
    }

    /// <summary>
    /// Person-scoped user attribute state (SPEC §6.1) — the outcome of an
    /// upsert. `MergedJson` is Postgres's canonical jsonb text form of the
    /// merged attributes; `NewHash` is its SHA-256 (used both as the
    /// change-detection gate and as the payload-of-record hash that the
    /// MoEngage customer sender writes back on delivery). `PreviousSyncedHash`
    /// is what was last successfully synced — the caller compares it with
    /// NewHash to decide whether to enqueue a `moengage_customer` delivery.
    /// </summary>
    public sealed record UserAttributesResult(string MergedJson, string NewHash, string? PreviousSyncedHash);

    /// <summary>
    /// Partial upsert of user attributes (SPEC §6.1). Keyed on (app_id, user_id)
    /// so tenants with overlapping account-id spaces stay isolated.
    /// `attributesJson` is the validated + normalized incoming block (may carry
    /// `null` values to clear stored keys — jsonb_strip_nulls collapses them).
    /// Both branches strip: on a first-ever upsert there is nothing to clear,
    /// so a `null` must not survive as a stored key — otherwise the row hashes
    /// non-empty, enqueues a MoEngage sync, and ships `{"email": null}` for a
    /// user who never set one. The merge branch reads the raw `$3` rather than
    /// EXCLUDED.attributes on purpose: EXCLUDED carries the already-stripped
    /// VALUES expression, which would drop the very nulls the merge needs as
    /// clear-this-key sentinels.
    /// </summary>
    public static async Task<UserAttributesResult> UpsertUserAttributesAsync(
        NpgsqlDataSource dataSource, string appId, string userId,
        string attributesJson, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        string mergedJson;
        string? previousSynced;
        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO user_attributes (app_id, user_id, attributes, updated_at)
            VALUES ($1, $2, jsonb_strip_nulls(coalesce($3::jsonb, '{}')), now())
            ON CONFLICT (app_id, user_id) DO UPDATE SET
                attributes = jsonb_strip_nulls(
                    user_attributes.attributes || coalesce($3::jsonb, '{}')),
                updated_at = now()
            RETURNING attributes::text, moengage_synced_hash
            """, conn, tx))
        {
            upsert.Parameters.Add(new() { Value = appId });
            upsert.Parameters.Add(new() { Value = userId });
            upsert.Parameters.Add(new() { Value = attributesJson, NpgsqlDbType = NpgsqlDbType.Jsonb });
            await using var reader = await upsert.ExecuteReaderAsync(ct);
            await reader.ReadAsync(ct);
            mergedJson = reader.GetString(0);
            previousSynced = await reader.IsDBNullAsync(1, ct) ? null : reader.GetString(1);
        }

        var newHash = Sha256Hex(mergedJson);
        await using (var updateHash = new NpgsqlCommand(
            "UPDATE user_attributes SET hash = $1 WHERE app_id = $2 AND user_id = $3", conn, tx))
        {
            updateHash.Parameters.Add(new() { Value = newHash });
            updateHash.Parameters.Add(new() { Value = appId });
            updateHash.Parameters.Add(new() { Value = userId });
            await updateHash.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new UserAttributesResult(mergedJson, newHash, previousSynced);
    }

    /// <summary>
    /// Look up the user_id last stored on this session (SPEC §6.1: an
    /// `attributes` block without a body-level user_id may fall back to the
    /// registry entry). Constrained to app_id so one tenant's SDK cannot
    /// resolve another tenant's session. Returns null when session or user_id
    /// absent.
    /// </summary>
    public static async Task<string?> LookupUserIdBySessionAsync(
        NpgsqlDataSource dataSource, string appId, Guid sessionKey, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT user_id FROM identity_registry WHERE app_id = $1 AND session_key = $2");
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = sessionKey });
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    /// <summary>
    /// Enqueues the reserved server event `ep_attributes_synced` for
    /// (app_id, user_id), routed to `moengage_customer` only (SPEC §6.1).
    /// Bypasses emit_event()'s reserved-name gate — this is the sole path
    /// that legitimately produces reserved events. Called by the /v1/identity
    /// handler when the attribute hash diverges from `moengage_synced_hash`
    /// and MoEngage attributes are enabled for that tenant.
    /// </summary>
    public static async Task EnqueueAttributesSyncAsync(
        NpgsqlDataSource dataSource, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            """
            WITH minted AS (
                INSERT INTO events_dedupe (event_id, app_id) VALUES (gen_random_uuid(), $1) RETURNING event_id
            ), outbox AS (
                INSERT INTO events_outbox
                    (app_id, event_id, event_name, origin, occurred_at, received_at,
                     user_id, anonymous_id, session_key, properties, context)
                SELECT $1, event_id, 'ep_attributes_synced', 'server', now(), now(),
                       $2, NULL, NULL, '{}'::jsonb, '{}'::jsonb
                FROM minted
                RETURNING id, received_at
            )
            INSERT INTO events_delivery (event_ref, received_at, app_id, destination)
            SELECT o.id, o.received_at, $1, 'moengage_customer' FROM outbox o
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Reads the current attributes JSON (Postgres canonical text) for a
    /// (app_id, user_id), or null when no row exists or the object is empty.
    /// Called by senders (GA4/Amplitude/Adjust) at send time to enrich outbound
    /// payloads with allowlisted user attributes (SPEC §6.1).
    /// </summary>
    public static async Task<string?> FetchUserAttributesJsonAsync(
        NpgsqlDataSource dataSource, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT attributes::text FROM user_attributes WHERE app_id = $1 AND user_id = $2 AND attributes <> '{}'::jsonb");
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    /// <summary>DSR deletion (SPEC §9.6). Idempotent — a missing row still returns success.</summary>
    public static async Task DeleteUserAttributesAsync(
        NpgsqlDataSource dataSource, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "DELETE FROM user_attributes WHERE app_id = $1 AND user_id = $2");
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string Sha256Hex(string s)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s), hash);
        return Convert.ToHexStringLower(hash);
    }

    private static NpgsqlParameter Nullable(string? value)
        => new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
