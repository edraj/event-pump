using EventPump.Api;
using Npgsql;
using NpgsqlTypes;

namespace EventPump.Data;

/// <summary>
/// Where a command runs: straight off the pool, or inside a transaction the
/// caller owns. Erasure (SPEC §9.7) has to write five tables all-or-nothing,
/// while every other statement in this file stands alone and does not care —
/// so <see cref="NpgsqlDataSource"/> converts implicitly and those call sites
/// read exactly as they did.
/// </summary>
public readonly struct SqlScope
{
    private readonly NpgsqlDataSource? _dataSource;
    private readonly NpgsqlConnection? _connection;
    private readonly NpgsqlTransaction? _transaction;

    private SqlScope(
        NpgsqlDataSource? dataSource, NpgsqlConnection? connection, NpgsqlTransaction? transaction)
    {
        _dataSource = dataSource;
        _connection = connection;
        _transaction = transaction;
    }

    public static implicit operator SqlScope(NpgsqlDataSource dataSource)
        => new(dataSource, null, null);

    /// <summary>Enlist in an open transaction; the caller commits.</summary>
    public static SqlScope In(NpgsqlConnection connection, NpgsqlTransaction transaction)
        => new(null, connection, transaction);

    public NpgsqlCommand CreateCommand(string sql)
        => _dataSource is { } source
            ? source.CreateCommand(sql)
            : new NpgsqlCommand(sql, _connection, _transaction);
}

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
            ON CONFLICT (app_id, event_id) DO NOTHING
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
            ON CONFLICT (app_id, session_key) DO UPDATE SET
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
                -- Per-destination handles (migration 0010) are user-scoped, not
                -- device-scoped: they name THIS person at GA4 / Amplitude / …
                -- So when identify() switches the session to a different
                -- user_id — an in-session account switch — the stored handles
                -- belong to the previous person and must not survive. Take
                -- whatever the switching call supplied (possibly NULL, which
                -- makes the sender fall back to the new generic user_id) and
                -- drop the rest. A call that repeats the same user_id, or
                -- carries none at all (setUserAttributes), still merges.
                moengage_customer_id     = CASE WHEN EXCLUDED.user_id IS NOT NULL AND EXCLUDED.user_id IS DISTINCT FROM identity_registry.user_id THEN EXCLUDED.moengage_customer_id
                                                ELSE coalesce(EXCLUDED.moengage_customer_id, identity_registry.moengage_customer_id) END,
                ga4_user_id              = CASE WHEN EXCLUDED.user_id IS NOT NULL AND EXCLUDED.user_id IS DISTINCT FROM identity_registry.user_id THEN EXCLUDED.ga4_user_id
                                                ELSE coalesce(EXCLUDED.ga4_user_id, identity_registry.ga4_user_id) END,
                amplitude_user_id        = CASE WHEN EXCLUDED.user_id IS NOT NULL AND EXCLUDED.user_id IS DISTINCT FROM identity_registry.user_id THEN EXCLUDED.amplitude_user_id
                                                ELSE coalesce(EXCLUDED.amplitude_user_id, identity_registry.amplitude_user_id) END,
                meta_external_id         = CASE WHEN EXCLUDED.user_id IS NOT NULL AND EXCLUDED.user_id IS DISTINCT FROM identity_registry.user_id THEN EXCLUDED.meta_external_id
                                                ELSE coalesce(EXCLUDED.meta_external_id, identity_registry.meta_external_id) END,
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
    /// Reads the previously-stored `moengage_customer_id` handle for a session.
    /// Used by the /v1/identity handler as a fallback when the setUserAttributes
    /// call itself didn't re-send the handle: without this lookup the sync
    /// enqueues with NULL and the MoEngage customer sender falls back to
    /// `user_id`, creating the two-profile split the handle was meant to
    /// prevent (SPEC §6.1, PR #8 review open-question #6).
    /// </summary>
    public static async Task<string?> LookupMoEngageCustomerIdBySessionAsync(
        NpgsqlDataSource dataSource, string appId, Guid sessionKey, CancellationToken ct)
    {
        await using var cmd = dataSource.CreateCommand(
            "SELECT moengage_customer_id FROM identity_registry WHERE app_id = $1 AND session_key = $2");
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
    ///
    /// A no-op when an undelivered sync is already queued for this user AND
    /// that job already carries the moengage_customer_id this call would stash:
    /// the caller's gate compares against `moengage_synced_hash`, which only
    /// moves on a successful delivery, so it keeps reporting "changed" for as
    /// long as a job is in flight.
    /// </summary>
    public static async Task EnqueueAttributesSyncAsync(
        NpgsqlDataSource dataSource, string appId, string userId,
        string? moengageCustomerId, CancellationToken ct)
    {
        // The reserved event has no session_key, so the MoEngageCustomerSender
        // cannot reach identity_registry at delivery time. We stash the
        // per-destination customer id in the row's context here so the sender
        // uses the same id the event sender does; otherwise MoEngage ends up
        // with two profiles per person (one from events, one from the sync).
        await using var cmd = dataSource.CreateCommand(
            """
            WITH pending AS (
                -- Skip when this user already has an undelivered sync queued.
                -- moengage_synced_hash only advances on a SUCCESSFUL delivery,
                -- so every /v1/identity call landing between enqueue and
                -- delivery sees "changed" again and piles on another job — a
                -- form saving field by field produces one per keystroke-group.
                -- They are pure waste, not stale, for the *attributes*:
                -- MoEngageCustomerSender re-reads user_attributes at send time,
                -- so the queued job carries the newest values.
                --
                -- The moengage_customer_id is the exception, and the reason for
                -- the last condition below. The reserved event has no
                -- session_key, so the sender cannot look that id up at delivery
                -- time — it only has what was stashed in this row's context.
                -- Attributes set before the user logs in queue a row carrying
                -- NULL; if the login that finally supplies the handle were then
                -- skipped as a duplicate, the only job in flight would still say
                -- NULL, the sender would fall back to user_id, and MoEngage
                -- would get the second profile the stash exists to prevent
                -- (PR #8 review open-question #6). So a queued job only counts
                -- as covering this call when it already carries the same id, or
                -- when this call brings no id of its own to add.
                --
                -- One statement rather than a read-then-write narrows the race
                -- to a single snapshot, but does not close it: under READ
                -- COMMITTED two concurrent calls each take their own snapshot,
                -- neither sees the other's uncommitted insert, and both enqueue.
                -- That lands back on the pre-existing duplicate, which is
                -- wasteful rather than wrong, so it is left alone.
                --
                -- The received_at bound keeps both partitioned tables pruned. A
                -- still-pending row cannot be older than the retry schedule
                -- allows (10 attempts, 1h backoff cap), so two days is well
                -- clear — and a worker down longer than that falls back to the
                -- duplicate, not to a missed sync.
                SELECT 1
                FROM events_delivery d
                JOIN events_outbox o
                  ON o.received_at = d.received_at AND o.id = d.event_ref
                WHERE d.app_id = $1
                  AND d.destination = 'moengage_customer'
                  AND d.status IN ('pending', 'failed')
                  AND d.received_at >= now() - interval '2 days'
                  AND o.app_id = $1
                  AND o.user_id = $2
                  AND o.event_name = 'ep_attributes_synced'
                  AND ($3::text IS NULL
                       OR o.context->>'moengage_customer_id' IS NOT DISTINCT FROM $3::text)
                LIMIT 1
            ), minted AS (
                INSERT INTO events_dedupe (event_id, app_id)
                SELECT gen_random_uuid(), $1
                WHERE NOT EXISTS (SELECT 1 FROM pending)
                RETURNING event_id
            ), outbox AS (
                INSERT INTO events_outbox
                    (app_id, event_id, event_name, origin, occurred_at, received_at,
                     user_id, anonymous_id, session_key, properties, context)
                SELECT $1, event_id, 'ep_attributes_synced', 'server', now(), now(),
                       $2, NULL, NULL, '{}'::jsonb,
                       CASE
                         WHEN $3::text IS NULL THEN '{}'::jsonb
                         ELSE jsonb_build_object('moengage_customer_id', $3::text)
                       END
                FROM minted
                RETURNING id, received_at
            )
            INSERT INTO events_delivery (event_ref, received_at, app_id, destination)
            SELECT o.id, o.received_at, $1, 'moengage_customer' FROM outbox o
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        cmd.Parameters.Add(Nullable(moengageCustomerId));
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

    /// <summary>
    /// One device Adjust knows this person by. The three fields are read off
    /// the *same* `identity_registry` row on purpose. `os` names nobody — it is
    /// what says whether `PlatformAdId` is an IDFA or a GAID — so taking it
    /// from whichever row happened to be newest sends an Android GAID under
    /// `idfa` for a person whose last session was on iOS. Adjust answers 200 to
    /// an id it holds nothing under, so that delivery reads `delivered` with
    /// the device never forgotten.
    /// </summary>
    public sealed record AdjustDevice(string? Adid, string? PlatformAdId, string? Os)
    {
        public bool HasId => Adid is not null || PlatformAdId is not null;
    }

    /// <summary>
    /// Every handle a destination might know this person by. The person-scoped
    /// ids are single, because the vendor holds one profile: a MoEngage
    /// customer, an Amplitude user, a GA4 client. The device-scoped ones are
    /// lists, because they are not: Adjust's forget-device API erases one
    /// device, and someone who used two phones has two of them. Keeping only
    /// the newest leaves the older phone tracked while the delivery records
    /// `delivered` and the audit trail reports Adjust complete.
    /// </summary>
    public sealed record ErasureHandles(
        string? MoEngageCustomerId = null,
        string? AmplitudeUserId = null,
        string? Ga4ClientId = null,
        string? Ga4UserId = null,
        IReadOnlyList<AdjustDevice>? AdjustDevices = null,
        IReadOnlyList<string>? AmplitudeDeviceIds = null)
    {
        public IReadOnlyList<AdjustDevice> Adjust => AdjustDevices ?? [];

        public IReadOnlyList<string> AmplitudeDevices => AmplitudeDeviceIds ?? [];

        // Key names match what the event senders write: deleting under a
        // different id reports success while leaving the real profile intact.
        public string ToContextJson()
        {
            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                WriteIfSet(writer, "moengage_customer_id", MoEngageCustomerId);
                WriteIfSet(writer, "amplitude_user_id", AmplitudeUserId);
                WriteIfSet(writer, "ga4_client_id", Ga4ClientId);
                WriteIfSet(writer, "ga4_user_id", Ga4UserId);
                if (Adjust.Count > 0)
                {
                    writer.WriteStartArray("adjust_devices");
                    foreach (var device in Adjust)
                    {
                        writer.WriteStartObject();
                        WriteIfSet(writer, "adid", device.Adid);
                        WriteIfSet(writer, "platform_ad_id", device.PlatformAdId);
                        WriteIfSet(writer, "os", device.Os);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                if (AmplitudeDevices.Count > 0)
                {
                    writer.WriteStartArray("amplitude_device_ids");
                    foreach (var deviceId in AmplitudeDevices) writer.WriteStringValue(deviceId);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        /// <summary>
        /// Reads back what <see cref="ToContextJson"/> wrote: the senders parse
        /// the queued erasure's context this way, and a repeat request parses
        /// the previous one's audit row when the registry rows it came from are
        /// already deleted. The scalar `adjust_adid` / `adjust_platform_ad_id` /
        /// `os` / `amplitude_device_id` keys are the shape written before the
        /// per-device fan-out; rows queued or audited by an older build still
        /// carry them, so they are read as a single device rather than dropped
        /// — an erasure that was already in flight across an upgrade must not
        /// lose the only handle that names the person.
        /// </summary>
        public static ErasureHandles FromContextJson(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var adjust = new List<AdjustDevice>();
            if (root.TryGetProperty("adjust_devices", out var devices)
                && devices.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var device in devices.EnumerateArray())
                {
                    var parsed = new AdjustDevice(
                        Field(device, "adid"),
                        Field(device, "platform_ad_id"),
                        Field(device, "os"));
                    if (parsed.HasId) adjust.Add(parsed);
                }
            }
            else
            {
                var adid = Field(root, "adjust_adid");
                var platformAdId = Field(root, "adjust_platform_ad_id");
                if (adid is not null || platformAdId is not null)
                    adjust.Add(new AdjustDevice(adid, platformAdId, Field(root, "os")));
            }

            var amplitudeDevices = new List<string>();
            if (root.TryGetProperty("amplitude_device_ids", out var deviceIds)
                && deviceIds.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var deviceId in deviceIds.EnumerateArray())
                    if (deviceId.ValueKind == System.Text.Json.JsonValueKind.String
                        && deviceId.GetString() is { } value)
                        amplitudeDevices.Add(value);
            }
            else if (Field(root, "amplitude_device_id") is { } legacyDeviceId)
            {
                amplitudeDevices.Add(legacyDeviceId);
            }

            return new ErasureHandles(
                Field(root, "moengage_customer_id"),
                Field(root, "amplitude_user_id"),
                Field(root, "ga4_client_id"),
                Field(root, "ga4_user_id"),
                adjust,
                amplitudeDevices);
        }

        /// <summary>
        /// Whether we can name this person at any destination. An `os` on its
        /// own is not a handle: a device carrying nothing else would otherwise
        /// look like a successful resolution and suppress the audit fallback.
        /// </summary>
        public bool HasAnyHandle =>
            MoEngageCustomerId is not null || AmplitudeUserId is not null
            || Ga4ClientId is not null || Ga4UserId is not null
            || Adjust.Any(device => device.HasId) || AmplitudeDevices.Count > 0;

        private static void WriteIfSet(
            System.Text.Json.Utf8JsonWriter writer, string key, string? value)
        {
            if (value is not null) writer.WriteString(key, value);
        }

        private static string? Field(System.Text.Json.JsonElement element, string key)
            => element.ValueKind == System.Text.Json.JsonValueKind.Object
               && element.TryGetProperty(key, out var value)
               && value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : null;
    }

    // Person-scoped handles are recorded per session and one can sit on a
    // different row than the newest activity, so most recent non-null wins per
    // column. Device-scoped handles are not collapsed that way: see
    // ResolveAdjustDevicesAsync and ResolveAmplitudeDevicesAsync.
    public static async Task<ErasureHandles> ResolveErasureHandlesAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT
              (array_agg(moengage_customer_id ORDER BY updated_at DESC)
                 FILTER (WHERE moengage_customer_id IS NOT NULL))[1],
              (array_agg(amplitude_user_id ORDER BY updated_at DESC)
                 FILTER (WHERE amplitude_user_id IS NOT NULL))[1],
              (array_agg(ga4_client_id ORDER BY updated_at DESC)
                 FILTER (WHERE ga4_client_id IS NOT NULL))[1],
              (array_agg(ga4_user_id ORDER BY updated_at DESC)
                 FILTER (WHERE ga4_user_id IS NOT NULL))[1]
            FROM identity_registry
            WHERE app_id = $1 AND user_id = $2
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });

        string? moengage = null, amplitudeUser = null, ga4Client = null, ga4User = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                string? At(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
                (moengage, amplitudeUser, ga4Client, ga4User) = (At(0), At(1), At(2), At(3));
            }
        }

        return new ErasureHandles(
            moengage, amplitudeUser, ga4Client, ga4User,
            await ResolveAdjustDevicesAsync(db, appId, userId, ct),
            await ResolveAmplitudeDevicesAsync(db, appId, userId, ct));
    }

    // One entry per device — Adjust forgets a device at a time, so a person's
    // older phone needs its own request or it stays tracked. Ordered by the id
    // that will be sent rather than by recency, because DISTINCT ON requires
    // its own key to lead the ORDER BY; nothing downstream depends on the
    // order, only on every device being in the list.
    //
    // The rows are grouped by the id that will be sent (the Adjust device id
    // when there is one, else the raw platform ad id), and within a group the
    // row carrying a platform ad id *and* an os wins over a merely newer one:
    // `os` is only meaningful alongside the ad id it was recorded with, and a
    // group whose newest row dropped both would otherwise lose the pairing.
    private static async Task<List<AdjustDevice>> ResolveAdjustDevicesAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT DISTINCT ON (device_key) adjust_adid, adjust_platform_ad_id, os
              FROM (
                SELECT coalesce(adjust_adid, adjust_platform_ad_id) AS device_key,
                       adjust_adid, adjust_platform_ad_id,
                       context->>'os' AS os, updated_at
                  FROM identity_registry
                 WHERE app_id = $1 AND user_id = $2
                   AND (adjust_adid IS NOT NULL OR adjust_platform_ad_id IS NOT NULL)
              ) devices
             ORDER BY device_key,
                      (adjust_platform_ad_id IS NOT NULL AND os IS NOT NULL) DESC,
                      updated_at DESC
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        var resolved = new List<AdjustDevice>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            string? At(int i) => reader.IsDBNull(i) ? null : reader.GetString(i);
            resolved.Add(new AdjustDevice(At(0), At(1), At(2)));
        }
        return resolved;
    }

    // Amplitude's deletion API takes `device_ids` as an array, so every device
    // this person used goes in one request. Keeping only the newest would
    // leave the pre-login events of every older device in place.
    private static async Task<List<string>> ResolveAmplitudeDevicesAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT amplitude_device_id
              FROM identity_registry
             WHERE app_id = $1 AND user_id = $2 AND amplitude_device_id IS NOT NULL
             GROUP BY amplitude_device_id
             ORDER BY max(updated_at) DESC
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        var resolved = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) resolved.Add(reader.GetString(0));
        return resolved;
    }

    // Live lookup first; when a previous erasure already removed the registry
    // rows, recover the handles from that erasure's audit record.
    public static async Task<ErasureHandles> ResolveErasureHandlesWithFallbackAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        var live = await ResolveErasureHandlesAsync(db, appId, userId, ct);
        if (live.HasAnyHandle) return live;
        if (await LastAuditedHandlesAsync(db, appId, userId, ct) is not { } json)
            return live;
        return ErasureHandles.FromContextJson(json);
    }

    // The anonymous ids this person's sessions were recorded under. Their
    // pre-login rows and events carry no user_id — this join is the only thing
    // that links them back — and they hold the ADID, device id, IP and
    // location that a GDPR request is mostly about. Scoped to rows the login
    // itself claimed (`user_id = $2`), and applied only to rows *nobody* has
    // claimed.
    //
    // An anonymous_id some *other* account has also logged in under is a
    // shared browser (`ep_aid` is per browser, not per person), and its
    // unclaimed rows are as likely to be the other account's pre-login
    // sessions as this person's. Erasing those would delete a second person's
    // data on the first person's request, so the whole id is left alone. The
    // cost is a residual gap this cannot close: a co-user who browsed the same
    // browser and never signed in leaves rows indistinguishable from the
    // requester's own pre-login ones, and those are still erased together.
    private const string PersonAnonymousIdsSql =
        """
        SELECT mine.anonymous_id FROM identity_registry mine
         WHERE mine.app_id = $1 AND mine.user_id = $2
           AND NOT EXISTS (
                 SELECT 1 FROM identity_registry shared
                  WHERE shared.app_id = $1
                    AND shared.anonymous_id = mine.anonymous_id
                    AND shared.user_id IS NOT NULL
                    AND shared.user_id <> $2)
        """;

    // Must run before the enqueue: a pending row landing after the downstream
    // delete re-creates exactly what was erased. Retention-bounded because a
    // row held `failed` behind breaker backoff outlives any shorter window.
    //
    // Pre-login events count. They carry no user_id, but they ship the same
    // device's ADID / client id, so one delivered after the erasure rebuilds
    // the very GA4 / Amplitude / Adjust profile we just deleted.
    public static async Task<int> CancelPendingDeliveriesAsync(
        SqlScope db, string appId, string userId,
        string[] destinations, int retentionDays, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"""
            UPDATE events_delivery d
               SET status = 'skipped', last_error = 'erased'
              FROM events_outbox o
             WHERE o.received_at = d.received_at
               AND o.id = d.event_ref
               AND d.app_id = $1
               AND o.app_id = $1
               AND (o.user_id = $2
                    OR (o.user_id IS NULL
                        AND o.anonymous_id IN ({PersonAnonymousIdsSql})))
               AND d.destination = ANY($3)
               AND d.status IN ('pending', 'failed')
               AND d.received_at >= now() - make_interval(days => $4::int)
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        cmd.Parameters.Add(new() { Value = destinations });
        cmd.Parameters.Add(new() { Value = retentionDays });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // The guard is per destination, not per request: a repeat call while
    // MoEngage is pending but Adjust went dead re-drives Adjust alone.
    public static async Task<(Guid? EventId, string[] Queued)> EnqueueErasureAsync(
        SqlScope db, string appId, string userId, string eventName,
        string[] destinations, string contextJson, int retentionDays, CancellationToken ct)
    {
        if (destinations.Length == 0) return (null, []);
        await using var cmd = db.CreateCommand(
            """
            WITH covered AS (
                SELECT d.destination
                  FROM events_delivery d
                  JOIN events_outbox o
                    ON o.received_at = d.received_at AND o.id = d.event_ref
                 WHERE d.app_id = $1
                   AND d.destination = ANY($4)
                   AND d.status IN ('pending', 'failed')
                   AND d.received_at >= now() - make_interval(days => $6::int)
                   AND o.app_id = $1
                   AND o.user_id = $2
                   AND o.event_name = $3::text
            ), todo AS (
                SELECT unnest($4) AS destination
                EXCEPT
                SELECT destination FROM covered
            ), minted AS (
                INSERT INTO events_dedupe (event_id, app_id)
                SELECT gen_random_uuid(), $1
                WHERE EXISTS (SELECT 1 FROM todo)
                RETURNING event_id
            ), outbox AS (
                INSERT INTO events_outbox
                    (app_id, event_id, event_name, origin, occurred_at, received_at,
                     user_id, anonymous_id, session_key, properties, context)
                SELECT $1, event_id, $3::text, 'server', now(), now(),
                       $2, NULL, NULL, '{}'::jsonb, $5::jsonb
                FROM minted
                RETURNING id, received_at, event_id
            )
            INSERT INTO events_delivery (event_ref, received_at, app_id, destination)
            SELECT o.id, o.received_at, $1, t.destination
              FROM outbox o CROSS JOIN todo t
            RETURNING destination,
                      (SELECT event_id FROM outbox)
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        cmd.Parameters.Add(new() { Value = eventName });
        cmd.Parameters.Add(new() { Value = destinations });
        cmd.Parameters.Add(new() { Value = contextJson, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new() { Value = retentionDays });
        var queued = new List<string>();
        Guid? eventId = null;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            queued.Add(reader.GetString(0));
            eventId = reader.GetGuid(1);
        }
        return (eventId, [.. queued]);
    }

    // Written for every erasure request, including ones that queue nothing:
    // proof that a request was handled has to exist even when there was no
    // destination to send it to. Survives retention — see 0012.
    public static async Task RecordErasureRequestAsync(
        SqlScope db, string appId, string userId, string variant,
        Guid? eventId, string handlesJson, string[] destinations,
        int cancelledDeliveries, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            INSERT INTO erasure_audit
                (app_id, user_id, variant, event_id, handles,
                 destinations, cancelled_deliveries)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6, $7)
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        cmd.Parameters.Add(new() { Value = variant });
        cmd.Parameters.Add(new()
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid,
            Value = (object?)eventId ?? DBNull.Value,
        });
        cmd.Parameters.Add(new()
        {
            Value = handlesJson,
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb,
        });
        cmd.Parameters.Add(new() { Value = destinations });
        cmd.Parameters.Add(new() { Value = cancelledDeliveries });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Called by the worker the moment a delivery reaches a terminal state.
    // The delivery row it came from is dropped at retention; this copy is not.
    public static async Task RecordErasureOutcomeAsync(
        SqlScope db, string appId, Guid eventId,
        string destination, string status, string? detail, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            UPDATE erasure_audit
               SET outcomes = outcomes || jsonb_build_object(
                     $3::text,
                     jsonb_build_object(
                       'status', $4::text,
                       'detail', $5::text,
                       'at', to_jsonb(now())))
             WHERE app_id = $1 AND event_id = $2
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid, Value = eventId });
        cmd.Parameters.Add(new() { Value = destination });
        cmd.Parameters.Add(new() { Value = status });
        cmd.Parameters.Add(Nullable(detail));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // identity_registry holds advertising ids, device ids, IP and location —
    // personal data under GDPR — and nothing ages it out: it is unpartitioned
    // and PartitionMaintenance never touches it. Leaving it behind would make
    // the erasure incomplete. Safe to delete here because the handles are
    // already stamped on the queued outbox row's context AND recorded on the
    // audit row, so a re-drive can still name the person downstream.
    //
    // The person's *pre-login* sessions go too: they carry no user_id, so
    // filtering on it alone would leave the same device's ADID and IP behind
    // forever. The self-referencing subquery reads the pre-statement snapshot,
    // so it is unaffected by the rows this DELETE is removing.
    public static async Task<int> DeleteIdentityRegistryAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            $"""
            DELETE FROM identity_registry
             WHERE app_id = $1
               AND (user_id = $2
                    OR (user_id IS NULL AND anonymous_id IN ({PersonAnonymousIdsSql})))
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // The handles recorded by the most recent erasure for this person. Once
    // identity_registry is deleted the live lookup returns nothing, so a repeat
    // erasure would fall back to our own user_id and delete nothing downstream.
    public static async Task<string?> LastAuditedHandlesAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT handles::text FROM erasure_audit
             WHERE app_id = $1 AND user_id = $2 AND handles - 'os' <> '{}'::jsonb
             ORDER BY requested_at DESC LIMIT 1
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        return (await cmd.ExecuteScalarAsync(ct)) as string;
    }

    public static async Task<List<string>> ReadErasureAuditAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            """
            SELECT jsonb_build_object(
                     'variant', variant,
                     'requested_at', to_jsonb(requested_at),
                     'handles', handles,
                     'destinations', to_jsonb(destinations),
                     'cancelled_deliveries', cancelled_deliveries,
                     'outcomes', outcomes)::text
              FROM erasure_audit
             WHERE app_id = $1 AND user_id = $2
             ORDER BY requested_at DESC
             LIMIT 100
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = userId });
        var rows = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(reader.GetString(0));
        return rows;
    }

    /// <summary>DSR deletion (SPEC §9.6). Idempotent — a missing row still returns success.</summary>
    public static async Task DeleteUserAttributesAsync(
        SqlScope db, string appId, string userId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
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
