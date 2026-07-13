using System.Threading.Channels;
using EventPump.Config;
using EventPump.Observability;
using Npgsql;
using NpgsqlTypes;

namespace EventPump.Worker;

/// <summary>
/// Claims due deliveries per destination (lease-based, FOR UPDATE SKIP LOCKED —
/// N instances safe) and pushes them through independent per-destination
/// pipelines (SPEC §11). One slow destination never blocks the others.
/// </summary>
public sealed class DeliveryWorker
{
    // Lease-based claim: the SELECT locks due rows, the UPDATE pushes their
    // next_attempt_at into the future, and the transaction commits immediately —
    // no transaction is held across HTTP sends, and a crashed worker's claims
    // self-release when the lease expires.
    private const string ClaimSql =
        """
        WITH claimed AS (
            SELECT received_at, event_ref, destination, attempts FROM events_delivery
            WHERE destination = $1
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
            RETURNING d.received_at, d.event_ref, d.destination, c.attempts
        )
        SELECT l.event_ref, l.received_at, l.destination, l.attempts,
               o.event_id, o.event_name, o.origin, o.occurred_at,
               o.user_id, o.anonymous_id, o.session_key,
               o.properties::text, o.context::text,
               ir.session_key IS NOT NULL AS has_identity,
               ir.anonymous_id, ir.user_id, ir.session_number,
               ir.ga4_client_id, ir.ga4_session_id, ir.firebase_app_instance_id,
               ir.amplitude_device_id, ir.adjust_adid, ir.adjust_platform_ad_id,
               ir.fbp, ir.fbc, ir.click_ids::text, ir.context::text, ir.client_ip
        FROM leased l
        JOIN events_outbox o ON o.received_at = l.received_at AND o.id = l.event_ref
        LEFT JOIN identity_registry ir ON ir.session_key = o.session_key
        """;

    private readonly EpConfig _config;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IReadOnlyList<IDestinationSender> _senders;
    private readonly Counter _deliveries;
    private readonly Gauge _pending;
    private readonly Gauge _circuit;
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
        _deliveries = metrics.Counter("deliveries_total", "Delivery outcomes.", "destination", "status");
        _pending = metrics.Gauge("outbox_pending", "Deliveries awaiting send or retry.", "destination");
        _circuit = metrics.Gauge("circuit_state", "1 while the destination circuit breaker is open.", "destination");
        _latency = metrics.Histogram("delivery_latency_seconds", "Destination send latency.",
            [0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10], "destination");
        _log = loggerFactory.CreateLogger("eventpump.worker");
    }

    /// <summary>Runs until cancelled, then drains in-flight sends and releases unsent claims.</summary>
    public async Task RunAsync(CancellationToken stopToken)
    {
        var pipelines = _senders.Select(sender => RunDestinationAsync(sender, stopToken)).ToList();
        pipelines.Add(RunMaintenanceAsync(stopToken));
        await Task.WhenAll(pipelines);
    }

    // ------------------------------------------------------------- pipeline

    private async Task RunDestinationAsync(IDestinationSender sender, CancellationToken stop)
    {
        var destination = sender.Destination;
        var breaker = new Breaker(
            _config.BreakerThreshold,
            TimeSpan.FromSeconds(_config.BreakerPauseSeconds),
            _circuit.WithLabels(destination));
        var channel = Channel.CreateBounded<DeliveryItem>(Math.Max(_config.ClaimBatchSize, 1) * 2);

        var consumers = Enumerable.Range(0, Math.Max(_config.SendConcurrency, 1))
            .Select(_ => ConsumeAsync(sender, channel.Reader, breaker, stop))
            .ToArray();

        await ClaimLoopAsync(destination, channel.Writer, breaker, stop);
        channel.Writer.Complete();
        await Task.WhenAll(consumers);
    }

    private async Task ClaimLoopAsync(
        string destination, ChannelWriter<DeliveryItem> writer, Breaker breaker, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (breaker.IsOpen)
                {
                    await SafeDelay(stop);
                    continue;
                }

                var items = await ClaimAsync(destination, CancellationToken.None);
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
                _log.LogWarning("claim loop error for {Destination}: {Error}", destination, ex.Message);
                await SafeDelay(stop);
            }
        }
    }

    private async Task ConsumeAsync(
        IDestinationSender sender, ChannelReader<DeliveryItem> reader, Breaker breaker, CancellationToken stop)
    {
        // Reader completes when the claimer exits; leftovers after stop are released.
        await foreach (var item in reader.ReadAllAsync(CancellationToken.None))
        {
            if (stop.IsCancellationRequested)
            {
                await ReleaseAsync([item]);
                continue;
            }

            while (breaker.IsOpen && !stop.IsCancellationRequested)
                await SafeDelay(stop);
            if (stop.IsCancellationRequested)
            {
                await ReleaseAsync([item]);
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
            _latency.WithLabels(item.Destination)
                .Observe(TimeProvider.System.GetElapsedTime(startedAt).TotalSeconds);

            try
            {
                await ApplyResultAsync(item, result, breaker);
            }
            catch (Exception ex)
            {
                _log.LogWarning("failed to record result for delivery {EventRef}/{Destination}: {Error}",
                    item.EventRef, item.Destination, ex.Message);
            }
        }
    }

    // -------------------------------------------------------------- results

    private async Task ApplyResultAsync(DeliveryItem item, SendResult result, Breaker breaker)
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
                    var delay = Backoff(attempts);
                    await UpdateAsync(item,
                        $"status = 'failed', attempts = $4, last_error = $5, next_attempt_at = now() + make_interval(secs => {delay.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})",
                        attempts, result.Detail);
                }
                breaker.Failure();
                break;
        }

        _deliveries.WithLabels(item.Destination, status).Inc();
        // SPEC: never log payloads — ids and states only.
        _log.LogInformation("delivery {EventId} {EventName} -> {Destination}: {Status} {Detail}",
            item.EventId, item.EventName, item.Destination, status, result.Detail ?? "");
    }

    private TimeSpan Backoff(int attempts)
    {
        var seconds = Math.Min(
            _config.BackoffBaseSeconds * Math.Pow(2, attempts - 1),
            _config.BackoffCapSeconds);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(seconds * jitter);
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
                _log.LogWarning("failed to release claim {EventRef}/{Destination}: {Error}",
                    item.EventRef, item.Destination, ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- claim

    private async Task<List<DeliveryItem>> ClaimAsync(string destination, CancellationToken ct)
    {
        var items = new List<DeliveryItem>();
        await using var cmd = _dataSource.CreateCommand(ClaimSql);
        cmd.Parameters.Add(new() { Value = destination });
        cmd.Parameters.Add(new() { Value = _config.ClaimBatchSize });
        cmd.Parameters.Add(new() { Value = (double)_config.LeaseSeconds });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            IdentitySnapshot? identity = null;
            if (reader.GetBoolean(13))
            {
                identity = new IdentitySnapshot(
                    reader.GetGuid(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15),
                    reader.IsDBNull(16) ? null : reader.GetInt32(16),
                    reader.IsDBNull(17) ? null : reader.GetString(17),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? null : reader.GetString(19),
                    reader.IsDBNull(20) ? null : reader.GetString(20),
                    reader.IsDBNull(21) ? null : reader.GetString(21),
                    reader.IsDBNull(22) ? null : reader.GetString(22),
                    reader.IsDBNull(23) ? null : reader.GetString(23),
                    reader.IsDBNull(24) ? null : reader.GetString(24),
                    reader.GetString(25),
                    reader.GetString(26),
                    reader.IsDBNull(27) ? null : reader.GetString(27));
            }
            items.Add(new DeliveryItem(
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
                identity));
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
                        "SELECT count(*) FROM events_delivery WHERE destination = $1 AND status IN ('pending', 'failed')");
                    cmd.Parameters.Add(new() { Value = sender.Destination });
                    _pending.WithLabels(sender.Destination)
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
