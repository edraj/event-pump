using System.Threading.Channels;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using Npgsql;
using NpgsqlTypes;

namespace EventPump.Worker;

/// <summary>
/// Claims due deliveries per (app_id, destination), lease-based, FOR UPDATE
/// SKIP LOCKED — N instances safe (SPEC v1.2 §11). Every tenant × destination
/// pair runs an independent pipeline with its own breaker: one tenant's GA4
/// outage never pauses another tenant's GA4, and one destination's stall
/// never blocks the others.
/// </summary>
public sealed class DeliveryWorker
{
    // Lease-based claim: the SELECT locks due rows, the UPDATE pushes their
    // next_attempt_at into the future, and the transaction commits immediately —
    // no transaction is held across HTTP sends, and a crashed worker's claims
    // self-release when the lease expires. Filter (destination, app_id) matches
    // the events_delivery_claim_idx partial index.
    private const string ClaimSql =
        """
        WITH claimed AS (
            SELECT received_at, event_ref, destination, attempts FROM events_delivery
            WHERE destination = $1
              AND app_id = $4
              AND status IN ('pending', 'failed')
              AND next_attempt_at <= now()
            ORDER BY next_attempt_at, event_ref
            LIMIT $2
            FOR UPDATE SKIP LOCKED
        ), leased AS (
            UPDATE events_delivery d
            SET next_attempt_at = now() + make_interval(secs => $3)
            FROM claimed c
            WHERE d.received_at = c.received_at
              AND d.event_ref = c.event_ref
              AND d.destination = c.destination
            RETURNING d.received_at, d.event_ref, d.destination, c.attempts, d.next_attempt_at
        )
        SELECT l.event_ref, l.received_at, l.destination, l.attempts,
               o.event_id, o.event_name, o.origin, o.occurred_at,
               o.user_id, o.anonymous_id, o.session_key,
               o.properties::text, o.context::text,
               ir.session_key IS NOT NULL AS has_identity,
               ir.session_key IS NOT NULL AND o.session_key IS NULL AS identity_by_user_id,
               ir.anonymous_id, ir.user_id, ir.session_number,
               ir.ga4_client_id, ir.ga4_session_id, ir.firebase_app_instance_id,
               ir.amplitude_device_id, ir.adjust_adid, ir.adjust_platform_ad_id,
               ir.fbp, ir.fbc, ir.click_ids::text, ir.context::text, ir.client_ip,
               ir.moengage_customer_id, ir.ga4_user_id, ir.amplitude_user_id, ir.meta_external_id,
               ir.updated_at, ir.context->>'os' AS identity_os,
               l.next_attempt_at AS lease_expires_at
        FROM leased l
        JOIN events_outbox o ON o.received_at = l.received_at AND o.id = l.event_ref
        LEFT JOIN LATERAL (
            -- Branch 1 — the event names a session: that row is the only
            -- correct answer. A miss is a race, not an absence (the identity
            -- POST has not landed yet); the NoIdentity grace window waits for
            -- it. Never falls through to branch 2 — borrowing a different
            -- session would pin the event to whatever device the person
            -- happened to use last.
            (SELECT r.*, 0 AS preference
             FROM identity_registry r
             WHERE r.app_id = o.app_id AND r.session_key = o.session_key)
            UNION ALL
            -- Branch 2 — no session to join on, so resolve the PERSON: their
            -- most recently active row ($5 = EP_IDENTITY_USER_FALLBACK).
            -- Guarded on o.session_key IS NULL so Postgres short-circuits it
            -- with a One-Time Filter for every client event. A NULL o.user_id
            -- makes the equality NULL and matches nothing, which is correct.
            --
            -- Deliberately unbounded by age. How stale a handle may be is a
            -- per-destination question, not a storage one: an amplitude
            -- device_id or a ga4_client_id ages harmlessly (the event carries
            -- user_id too, so the person stays right), while an adjust_adid
            -- names an install and carries its attribution. Bounding here
            -- would deny Amplitude and GA4 a perfectly good row to protect
            -- Adjust, so the row goes out with its updated_at and AdjustSender
            -- makes that call for itself.
            (SELECT r.*, 1 AS preference
             FROM identity_registry r
             WHERE o.session_key IS NULL AND $5
               AND r.app_id = o.app_id AND r.user_id = o.user_id
             -- session_key breaks ties. Equal updated_at is ordinary (a
             -- backfill, an import, two upserts in the same tick) and without
             -- a tiebreaker the winner is whichever the scan reaches first,
             -- so consecutive claims for one person can pick different
             -- devices and split them downstream. It is in the index, so the
             -- ORDER BY still costs no sort.
             ORDER BY r.updated_at DESC, r.session_key DESC
             LIMIT 1)
            -- Written as two independent branches rather than one CASE
            -- predicate on purpose: a CASE is opaque to the planner, which
            -- then scans every identity row for the tenant and sorts them on
            -- EVERY delivery — measured at 4.3s per 50-row claim batch against
            -- 400k identity rows, versus ~1ms here. Branch 1 uses the
            -- (app_id, session_key) primary key, branch 2 uses
            -- identity_registry_person_idx (migration 0013).
            ORDER BY preference
            LIMIT 1
        ) ir ON true
        """;

    private readonly EpConfig _config;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<IDestinationSender> _senders;
    private readonly Counter _deliveries;
    private readonly Gauge _pending;
    private readonly Gauge _circuit;
    private readonly Gauge _auth;
    private readonly Histogram _latency;
    private readonly ILogger _log;

    public DeliveryWorker(
        EpConfig config,
        NpgsqlDataSource dataSource,
        IReadOnlyList<IDestinationSender> senders,
        MetricsRegistry metrics,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _dataSource = dataSource;
        _senders = senders;
        _deliveries = metrics.Counter("deliveries_total",
            "Delivery outcomes.", "app_id", "destination", "status");
        _pending = metrics.Gauge("outbox_pending",
            "Deliveries awaiting send or retry.", "app_id", "destination");
        _circuit = metrics.Gauge("circuit_state",
            "1 while the (app_id, destination) circuit breaker is open.", "app_id", "destination");
        _auth = metrics.Gauge("auth_state",
            "1 while the (app_id, destination) pipeline is paused because the destination "
            + "rejected our credentials.", "app_id", "destination");
        _latency = metrics.Histogram("delivery_latency_seconds",
            "Destination send latency.",
            [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10], "app_id", "destination");
        _log = loggerFactory.CreateLogger("eventpump.worker");
        WarnIfLeaseCannotCoverTheQueue();
    }

    private void WarnIfLeaseCannotCoverTheQueue()
    {
        var concurrency = Math.Max(_config.SendConcurrency, 1);
        var maxHeld = (concurrency * 2) + Math.Max(_config.ClaimBatchSize, 1) + concurrency;
        var worstCaseSeconds =
            Math.Ceiling((double)maxHeld / concurrency) * (_config.SenderTimeoutMs / 1000.0);
        if (worstCaseSeconds <= _config.LeaseSeconds) return;
        _log.LogWarning(
            "worker tuning: up to {MaxHeld} claimed deliveries may be held at once and draining them at the "
            + "{TimeoutMs}ms sender timeout takes up to {WorstCase}s, beyond the {Lease}s lease — a slow "
            + "destination will let leases expire while items wait, and re-claimed items are delivered twice "
            + "to destinations that do not de-duplicate (ga4, moengage, adjust). Lower EP_WORKER_CLAIM_BATCH, "
            + "raise EP_WORKER_SEND_CONCURRENCY, or raise EP_WORKER_LEASE_S.",
            maxHeld, _config.SenderTimeoutMs, Math.Round(worstCaseSeconds), _config.LeaseSeconds);
    }

    /// <summary>Runs until cancelled, then drains in-flight sends and releases unsent claims.</summary>
    public async Task RunAsync(CancellationToken stopToken)
    {
        var pipelines = _senders.Select(sender => RunPipelineAsync(sender, stopToken)).ToList();
        pipelines.Add(RunMaintenanceAsync(stopToken));
        await Task.WhenAll(pipelines);
    }

    // ------------------------------------------------------------- pipeline

    private async Task RunPipelineAsync(IDestinationSender sender, CancellationToken stop)
    {
        var appId = sender.AppId;
        var destination = sender.Destination;
        var breaker = new Breaker(
            _config.BreakerThreshold,
            TimeSpan.FromSeconds(_config.BreakerPauseSeconds),
            _circuit.WithLabels(appId, destination));
        var auth = new AuthLatch(
            TimeSpan.FromSeconds(_config.AuthPauseSeconds),
            _auth.WithLabels(appId, destination));

        var channel = Channel.CreateBounded<DeliveryItem>(Math.Max(_config.SendConcurrency, 1) * 2);

        var consumers = Enumerable.Range(0, Math.Max(_config.SendConcurrency, 1))
            .Select(_ => ConsumeAsync(sender, channel.Reader, breaker, auth, stop))
            .ToArray();

        await ClaimLoopAsync(appId, destination, channel.Writer, breaker, auth, stop);
        channel.Writer.Complete();
        await Task.WhenAll(consumers);
    }

    private async Task ClaimLoopAsync(
        string appId, string destination, ChannelWriter<DeliveryItem> writer,
        Breaker breaker, AuthLatch auth, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                // The auth latch pauses alongside the breaker but means the
                // opposite thing: the destination is up and is refusing us.
                // Pausing is what keeps a wrong key from costing one rejected
                // request per queued row — the backlog waits instead, and a
                // single probe per window finds out when the key is fixed.
                if (breaker.IsOpen || auth.IsPaused)
                {
                    await SafeDelay(stop);
                    continue;
                }

                var items = await ClaimAsync(appId, destination, CancellationToken.None);
                if (items.Count == 0)
                {
                    await SafeDelay(stop);
                    continue;
                }

                var queued = 0;
                try
                {
                    foreach (var item in items)
                    {
                        await writer.WriteAsync(item, stop);
                        queued++;
                    }
                }
                catch (OperationCanceledException)
                {
                    await ReleaseAsync(items.Skip(queued));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("claim loop error for {AppId}/{Destination}: {Error}",
                    appId, destination, ex.Message);
                await SafeDelay(stop);
            }
        }
    }

    private async Task ConsumeAsync(
        IDestinationSender sender, ChannelReader<DeliveryItem> reader, Breaker breaker,
        AuthLatch auth, CancellationToken stop)
    {
        // Reader completes when the claimer exits; leftovers after stop are released.
        await foreach (var item in reader.ReadAllAsync(CancellationToken.None))
        {
            if (stop.IsCancellationRequested)
            {
                await ReleaseAsync([item]);
                continue;
            }

            while ((breaker.IsOpen || auth.IsPaused) && !stop.IsCancellationRequested)
                await SafeDelay(stop);
            if (stop.IsCancellationRequested)
            {
                await ReleaseAsync([item]);
                continue;
            }

            if (item.LeaseExpiresAt is { } leaseExpiresAt
                && DateTime.UtcNow.AddMilliseconds(_config.SenderTimeoutMs) >= leaseExpiresAt)
            {
                _deliveries.WithLabels(item.AppId, item.Destination, "lease_expired").Inc();
                _log.LogWarning(
                    "lease expired before send for {EventRef}/{AppId}/{Destination}; leaving it to be re-claimed "
                    + "(queue is draining slower than EP_WORKER_LEASE_S allows)",
                    item.EventRef, item.AppId, item.Destination);
                continue;
            }

            // A DSR erasure can have cancelled this delivery while it waited
            // here: ClaimSql leases a row by pushing next_attempt_at forward
            // and leaves its status `pending`, so CancelPendingDeliveriesAsync
            // flips the row we are holding to `skipped` and counts it in
            // `cancelled_deliveries`. Nothing in the in-memory copy can tell.
            // Without this re-read the send still goes out — up to a full
            // lease after the erasure committed — rebuilding at the
            // destination the profile that was just deleted, while
            // ApplyResultAsync's `status IN ('pending','failed')` guard
            // discards the outcome and the row goes on reading
            // `skipped: erased`. One indexed lookup per delivery is what makes
            // the cancellation count true rather than aspirational.
            var claimable = await StillClaimableAsync(item);
            if (claimable is not true)
            {
                // `cancelled` is a fact about the row; a read we could not
                // make is a fact about us. Sharing a label would let a
                // database blip inflate the count an operator reads as "this
                // many DSR erasures stopped a send".
                _deliveries.WithLabels(item.AppId, item.Destination,
                    claimable is false ? "cancelled" : "deferred").Inc();
                continue;
            }

            var startedAt = TimeProvider.System.GetTimestamp();
            SendResult result;
            try
            {
                result = await sender.SendAsync(item, CancellationToken.None);
            }
            catch (Exception ex)
            {
                result = SendResult.Retry($"{ex.GetType().Name}: {ex.Message}");
            }
            _latency.WithLabels(item.AppId, item.Destination)
                .Observe(TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds);

            try
            {
                await ApplyResultAsync(item, result, breaker, auth);
            }
            catch (Exception ex)
            {
                _log.LogWarning("failed to record result for delivery {EventRef}/{AppId}/{Destination}: {Error}",
                    item.EventRef, item.AppId, item.Destination, ex.Message);
            }
        }
    }

    // -------------------------------------------------------------- results

    private async Task ApplyResultAsync(
        DeliveryItem item, SendResult result, Breaker breaker, AuthLatch auth)
    {
        string status;
        switch (result.Outcome)
        {
            case SendOutcome.Delivered:
                status = "delivered";
                await UpdateAsync(item,
                    "status = 'delivered', delivered_at = now(), attempts = $4, last_error = NULL",
                    item.Attempts + 1, null);
                breaker.Success();
                auth.Clear(); // the credential works; drop the latch and the gauge
                break;

            case SendOutcome.Skip:
                status = "skipped";
                await UpdateAsync(item, "status = 'skipped', attempts = $4, last_error = $5",
                    item.Attempts, result.Detail);
                breaker.Success();
                break;

            case SendOutcome.Dead:
                status = "dead";
                await UpdateAsync(item, "status = 'dead', attempts = $4, last_error = $5",
                    item.Attempts + 1, result.Detail);
                breaker.Success(); // permanent rejection is not a destination outage
                break;

            case SendOutcome.NoIdentity:
                var identityAttempts = item.Attempts + 1;
                if (DateTime.UtcNow - item.ReceivedAt < TimeSpan.FromSeconds(_config.IdentityGraceSeconds)
                    && identityAttempts < _config.MaxAttempts)
                {
                    status = "failed";
                    await ScheduleRetryAsync(item, identityAttempts, result.Detail);
                }
                else
                {
                    status = "skipped";
                    await UpdateAsync(item, "status = 'skipped', attempts = $4, last_error = $5",
                        item.Attempts, result.Detail);
                }
                breaker.Success(); // a missing identity is not a destination outage
                break;

            case SendOutcome.AuthFailed:
                // Retry like a transient fault so a corrected key drains the
                // backlog, but never through the breaker: the destination is
                // healthy and is refusing us, and reporting that as an outage
                // sends an operator to the wrong dashboard. The latch below is
                // what stops this from becoming one rejected request per row.
                var authAttempts = item.Attempts + 1;
                if (authAttempts >= _config.MaxAttempts)
                {
                    status = "dead";
                    await UpdateAsync(item, "status = 'dead', attempts = $4, last_error = $5",
                        authAttempts, result.Detail);
                }
                else
                {
                    status = "auth_failed";
                    await ScheduleRetryAsync(item, authAttempts, result.Detail);
                }
                breaker.Success(); // a refused credential is not a destination outage
                if (auth.Trip())
                {
                    // Once per latch window, at Error: this is the signal that
                    // tells an operator a key is wrong. Without it the only
                    // evidence is rows quietly retrying and a metric nobody
                    // alerts on yet.
                    _log.LogError(
                        "{AppId}/{Destination} rejected our credentials ({Detail}); pausing this pipeline "
                        + "for {Pause}s. Deliveries keep their retry budget and drain once the credential is "
                        + "fixed — check this tenant's key. auth_state{{app_id=\"{AppId}\",destination=\"{Destination}\"}} is 1.",
                        item.AppId, item.Destination, result.Detail, _config.AuthPauseSeconds,
                        item.AppId, item.Destination);
                }
                break;

            default: // Retry
                var attempts = item.Attempts + 1;
                if (attempts >= _config.MaxAttempts)
                {
                    status = "dead";
                    await UpdateAsync(item, "status = 'dead', attempts = $4, last_error = $5",
                        attempts, result.Detail);
                }
                else
                {
                    status = "failed";
                    await ScheduleRetryAsync(item, attempts, result.Detail);
                }
                breaker.Failure();
                break;
        }

        _deliveries.WithLabels(item.AppId, item.Destination, status).Inc();
        // SPEC: never log payloads — ids and states only.
        _log.LogInformation("delivery {EventId} {EventName} -> {AppId}/{Destination}: {Status} {Detail}",
            item.EventId, item.EventName, item.AppId, item.Destination, status, result.Detail ?? "");

        // After the metric and the log line, not before: this is the only
        // write here that can throw on a transient database fault, and doing
        // it first would take the delivery's metric and log record down with
        // it — losing the two signals that would tell an operator the audit
        // row is the thing that went missing.
        // `failed` and `auth_failed` are both still-retrying rows, so neither
        // has an outcome to record yet. Keyed on the retrying states rather
        // than on a list of terminal ones so a new terminal status cannot
        // silently stop writing the audit row.
        if (status is not ("failed" or "auth_failed") && TrackingPlan.IsErasureDestination(item.Destination))
        {
            await EventStore.RecordErasureOutcomeAsync(
                _dataSource, item.AppId, item.EventId, item.Destination,
                status, result.Detail, CancellationToken.None);
        }
    }

    private Task ScheduleRetryAsync(DeliveryItem item, int attempts, string? lastError)
    {
        var delay = Backoff(attempts);
        return UpdateAsync(item,
            $"status = 'failed', attempts = $4, last_error = $5, next_attempt_at = now() + make_interval(secs => {delay.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})",
            attempts, lastError);
    }

    private TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(
            _config.BackoffBaseSeconds * Math.Pow(2, attempts - 1),
            _config.BackoffCapSeconds);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(seconds * jitter);
    }

    /// <summary>
    /// Whether the leased row is still one we may send: `true` yes, `false`
    /// cancelled out from under us (see the call site), `null` we could not
    /// find out. The last two both hold the send back — an erasure may have
    /// revoked it — but only the middle one is a cancellation, and the row is
    /// left to be re-claimed when the lease expires either way.
    /// </summary>
    private async Task<bool?> StillClaimableAsync(DeliveryItem item)
    {
        try
        {
            await using var cmd = _dataSource.CreateCommand(
                """
                SELECT 1 FROM events_delivery
                 WHERE received_at = $1 AND event_ref = $2 AND destination = $3
                   AND status IN ('pending', 'failed')
                """);
            cmd.Parameters.Add(new() { Value = item.ReceivedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            cmd.Parameters.Add(new() { Value = item.EventRef });
            cmd.Parameters.Add(new() { Value = item.Destination });
            return await cmd.ExecuteScalarAsync(CancellationToken.None) is not null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(
                "could not confirm delivery {EventRef}/{AppId}/{Destination} before sending: {Error}; "
                + "leaving it to be re-claimed",
                item.EventRef, item.AppId, item.Destination, ex.Message);
            return null;
        }
    }

    private async Task UpdateAsync(DeliveryItem item, string setClause, int attempts, string? lastError)
    {
        await using var cmd = _dataSource.CreateCommand(
            $"""
            UPDATE events_delivery SET {setClause}
            WHERE received_at = $1 AND event_ref = $2 AND destination = $3
              AND status IN ('pending', 'failed')
            """);
        cmd.Parameters.Add(new() { Value = item.ReceivedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new() { Value = item.EventRef });
        cmd.Parameters.Add(new() { Value = item.Destination });
        cmd.Parameters.Add(new() { Value = attempts });
        if (setClause.Contains("$5"))
            cmd.Parameters.Add(new() { Value = (object?)lastError ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task ReleaseAsync(IEnumerable<DeliveryItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                await using var cmd = _dataSource.CreateCommand(
                    """
                    UPDATE events_delivery SET next_attempt_at = now()
                    WHERE received_at = $1 AND event_ref = $2 AND destination = $3
                      AND status IN ('pending', 'failed')
                    """);
                cmd.Parameters.Add(new() { Value = item.ReceivedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz });
                cmd.Parameters.Add(new() { Value = item.EventRef });
                cmd.Parameters.Add(new() { Value = item.Destination });
                await cmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogWarning("failed to release claim {EventRef}/{AppId}/{Destination}: {Error}",
                    item.EventRef, item.AppId, item.Destination, ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- claim

    private async Task<List<DeliveryItem>> ClaimAsync(string appId, string destination, CancellationToken ct)
    {
        var items = new List<DeliveryItem>();
        await using var cmd = _dataSource.CreateCommand(ClaimSql);
        cmd.Parameters.Add(new() { Value = destination });
        cmd.Parameters.Add(new() { Value = _config.ClaimBatchSize });
        cmd.Parameters.Add(new() { Value = (double)_config.LeaseSeconds });
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(new() { Value = _config.IdentityUserFallback });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            IdentitySnapshot? identity = null;
            if (reader.GetBoolean(13))
            {
                var byUserId = reader.GetBoolean(14);
                identity = new IdentitySnapshot(
                    reader.GetGuid(15),
                    reader.IsDBNull(16) ? null : reader.GetString(16),
                    byUserId || reader.IsDBNull(17) ? null : reader.GetInt32(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    byUserId || reader.IsDBNull(19) ? null : reader.GetString(19),
                    reader.IsDBNull(20) ? null : reader.GetString(20),
                    reader.IsDBNull(21) ? null : reader.GetString(21),
                    reader.IsDBNull(22) ? null : reader.GetString(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23),
                    reader.IsDBNull(24) ? null : reader.GetString(24),
                    reader.IsDBNull(25) ? null : reader.GetString(25),
                    reader.GetString(26),
                    byUserId ? "{}" : reader.GetString(27),
                    byUserId || reader.IsDBNull(28) ? null : reader.GetString(28),
                    reader.IsDBNull(29) ? null : reader.GetString(29),
                    reader.IsDBNull(30) ? null : reader.GetString(30),
                    reader.IsDBNull(31) ? null : reader.GetString(31),
                    reader.IsDBNull(32) ? null : reader.GetString(32),
                    byUserId,
                    reader.IsDBNull(33) ? null : reader.GetDateTime(33),
                    reader.IsDBNull(34) ? null : reader.GetString(34));
            }
            items.Add(new DeliveryItem(
                appId,
                reader.GetInt64(0),
                reader.GetDateTime(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetGuid(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetGuid(9),
                reader.IsDBNull(10) ? null : reader.GetGuid(10),
                reader.GetString(11),
                reader.GetString(12),
                identity,
                reader.GetDateTime(35)));
        }
        return items;
    }

    // ---------------------------------------------------------- maintenance

    private async Task RunMaintenanceAsync(CancellationToken stop)
    {
        var lastPartitionRun = DateTime.MinValue;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - lastPartitionRun >= TimeSpan.FromHours(1))
                {
                    await PartitionMaintenance.RunOnceAsync(
                        _dataSource, _config.RetentionDays, _config.RetentionDeadDays, 3, CancellationToken.None);
                    lastPartitionRun = DateTime.UtcNow;
                }
                foreach (var sender in _senders)
                {
                    await using var cmd = _dataSource.CreateCommand(
                        "SELECT count(*) FROM events_delivery WHERE app_id = $1 AND destination = $2 AND status IN ('pending', 'failed')");
                    cmd.Parameters.Add(new() { Value = sender.AppId });
                    cmd.Parameters.Add(new() { Value = sender.Destination });
                    _pending.WithLabels(sender.AppId, sender.Destination)
                        .Set((long)(await cmd.ExecuteScalarAsync(CancellationToken.None))!);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("maintenance error: {Error}", ex.Message);
            }
            await SafeDelay(stop, 10);
        }
    }

    private async Task SafeDelay(CancellationToken stop, int multiplier = 1)
    {
        try
        {
            await Task.Delay(Math.Max(_config.WorkerPollMs, 10) * multiplier, stop);
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ----------------------------------------------------------- auth latch

    /// <summary>
    /// Pauses one (app_id, destination) pipeline while the destination is
    /// refusing our credentials.
    ///
    /// Separate from <see cref="Breaker"/> on purpose. The breaker's gauge is
    /// an outage signal — operators page on it — and a wrong key in a tenant
    /// file is not an outage. It also trips on a *count* of failures, where one
    /// refused credential already tells us everything: every other row for this
    /// pair will be refused the same way, so the first one should stop the
    /// pipeline rather than the fifth.
    ///
    /// A single failure latches. The pause expires on its own so a fixed key
    /// is picked up without a restart, and the first delivery after it clears
    /// the latch.
    /// </summary>
    private sealed class AuthLatch(TimeSpan pause, GaugeChild gauge)
    {
        private readonly object _lock = new();
        private DateTime _pausedUntil = DateTime.MinValue;

        public bool IsPaused
        {
            get
            {
                lock (_lock)
                {
                    if (_pausedUntil > DateTime.UtcNow) return true;
                    gauge.Set(0);
                    return false;
                }
            }
        }

        /// <summary>Latches; true when this call is what started a new pause window (log once).</summary>
        public bool Trip()
        {
            lock (_lock)
            {
                var wasPaused = _pausedUntil > DateTime.UtcNow;
                _pausedUntil = DateTime.UtcNow + pause;
                gauge.Set(1);
                return !wasPaused;
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _pausedUntil = DateTime.MinValue;
                gauge.Set(0);
            }
        }
    }

    // -------------------------------------------------------------- breaker

    /// <summary>N consecutive retryable failures open the circuit for the pause window.</summary>
    private sealed class Breaker(int threshold, TimeSpan pause, GaugeChild gauge)
    {
        private readonly object _lock = new();
        private int _consecutive;
        private DateTime _openUntil = DateTime.MinValue;

        public bool IsOpen
        {
            get
            {
                lock (_lock)
                {
                    if (_openUntil > DateTime.UtcNow) return true;
                    gauge.Set(0);
                    return false;
                }
            }
        }

        public void Success()
        {
            lock (_lock) _consecutive = 0;
        }

        public void Failure()
        {
            lock (_lock)
            {
                if (++_consecutive < threshold) return;
                _openUntil = DateTime.UtcNow + pause;
                // half-open probe: one more failure after the pause reopens immediately
                _consecutive = threshold - 1;
                gauge.Set(1);
            }
        }
    }
}
