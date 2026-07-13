using EventPump.Config;
using EventPump.Observability;
using EventPump.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class WorkerTests(PostgresFixture pg)
{
    private static EpConfig FastConfig(int maxAttempts = 10, int breakerThreshold = 100) => new()
    {
        DbConnString = "unused-in-tests",
        WorkerPollMs = 50,
        ClaimBatchSize = 10,
        SendConcurrency = 1,
        BackoffBaseSeconds = 0,
        BackoffCapSeconds = 0,
        MaxAttempts = maxAttempts,
        BreakerThreshold = breakerThreshold,
        BreakerPauseSeconds = 60,
        LeaseSeconds = 300,
    };

    private sealed class FakeSender(string destination, Func<DeliveryItem, Task<SendResult>> handler)
        : IDestinationSender
    {
        public string Destination => destination;
        public Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct) => handler(item);
    }

    private static async Task WaitFor(Func<Task<bool>> condition, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(50);
        }
        Assert.Fail("condition not met within timeout");
    }

    private static async Task RunWorkerUntil(
        NpgsqlDataSource ds, EpConfig cfg, IDestinationSender[] senders,
        MetricsRegistry metrics, Func<Task<bool>> condition)
    {
        var worker = new DeliveryWorker(cfg, ds, senders, metrics, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        try
        {
            await WaitFor(condition);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }

    [Fact]
    public async Task Delivers_pending_rows_and_counts_them()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "thing_happened", "server", "fake");
        for (var i = 0; i < 5; i++) await Db.Emit(ds, "thing_happened");

        var seenNames = new List<string>();
        var sender = new FakeSender("fake", item =>
        {
            lock (seenNames) seenNames.Add(item.EventName);
            return Task.FromResult(SendResult.Delivered());
        });
        var metrics = new MetricsRegistry();

        await RunWorkerUntil(ds, FastConfig(), [sender], metrics, async () =>
            await Db.Scalar<long>(ds,
                "SELECT count(*) FROM events_delivery WHERE status = 'delivered' AND delivered_at IS NOT NULL") == 5);

        Assert.Equal(5, seenNames.Count);
        Assert.All(seenNames, n => Assert.Equal("thing_happened", n));
        Assert.Contains("""deliveries_total{destination="fake",status="delivered"} 5""", metrics.Render());
    }

    [Fact]
    public async Task Retries_then_marks_dead_after_max_attempts()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "thing_happened", "server", "fake");
        await Db.Emit(ds, "thing_happened");

        var sender = new FakeSender("fake", _ => Task.FromResult(SendResult.Retry("boom")));

        await RunWorkerUntil(ds, FastConfig(maxAttempts: 3), [sender], new MetricsRegistry(), async () =>
            await Db.Scalar<long>(ds, "SELECT count(*) FROM events_delivery WHERE status = 'dead'") == 1);

        Assert.Equal(3, (int)await Db.Scalar<int>(ds, "SELECT attempts FROM events_delivery LIMIT 1"));
        Assert.Equal("boom", await Db.Scalar<string>(ds, "SELECT last_error FROM events_delivery LIMIT 1"));
    }

    [Fact]
    public async Task Skip_is_terminal_with_reason()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "thing_happened", "server", "fake");
        await Db.Emit(ds, "thing_happened");

        var sender = new FakeSender("fake", _ => Task.FromResult(SendResult.Skip("no_adjust_adid")));
        var metrics = new MetricsRegistry();

        await RunWorkerUntil(ds, FastConfig(), [sender], metrics, async () =>
            await Db.Scalar<long>(ds, "SELECT count(*) FROM events_delivery WHERE status = 'skipped'") == 1);

        Assert.Equal("no_adjust_adid", await Db.Scalar<string>(ds, "SELECT last_error FROM events_delivery LIMIT 1"));
        Assert.Contains("""deliveries_total{destination="fake",status="skipped"} 1""", metrics.Render());
    }

    [Fact]
    public async Task Graceful_stop_finishes_in_flight_and_releases_claims()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "thing_happened", "server", "fake");
        for (var i = 0; i < 5; i++) await Db.Emit(ds, "thing_happened");

        var firstSendStarted = new TaskCompletionSource();
        var releaseFirstSend = new TaskCompletionSource<SendResult>();
        var sends = 0;
        var sender = new FakeSender("fake", _ =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                firstSendStarted.TrySetResult();
                return releaseFirstSend.Task; // in-flight until we say so
            }
            return Task.FromResult(SendResult.Delivered());
        });

        var worker = new DeliveryWorker(FastConfig(), ds, [sender], new MetricsRegistry(), NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);

        await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cts.Cancel();
        releaseFirstSend.SetResult(SendResult.Delivered());
        await run.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(1L, await Db.Scalar<long>(ds,
            "SELECT count(*) FROM events_delivery WHERE status = 'delivered'"));
        // the other claimed-but-unsent rows were released: pending and due now
        Assert.Equal(4L, await Db.Scalar<long>(ds,
            "SELECT count(*) FROM events_delivery WHERE status = 'pending' AND next_attempt_at <= now()"));
    }

    [Fact]
    public async Task Circuit_breaker_pauses_one_destination_without_blocking_others()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "thing_happened", "server", "flaky", "steady");
        for (var i = 0; i < 5; i++) await Db.Emit(ds, "thing_happened");

        var flaky = new FakeSender("flaky", _ => Task.FromResult(SendResult.Retry("down")));
        var steady = new FakeSender("steady", _ => Task.FromResult(SendResult.Delivered()));
        var metrics = new MetricsRegistry();

        var worker = new DeliveryWorker(
            FastConfig(breakerThreshold: 2), ds, [flaky, steady], metrics, NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        try
        {
            await WaitFor(async () =>
                await Db.Scalar<long>(ds,
                    "SELECT count(*) FROM events_delivery WHERE destination = 'steady' AND status = 'delivered'") == 5
                && metrics.Render().Contains("""circuit_state{destination="flaky"} 1"""));

            // while the breaker is open, flaky attempts stop growing
            var before = await Db.Scalar<long>(ds,
                "SELECT coalesce(sum(attempts), 0) FROM events_delivery WHERE destination = 'flaky'");
            await Task.Delay(400);
            var after = await Db.Scalar<long>(ds,
                "SELECT coalesce(sum(attempts), 0) FROM events_delivery WHERE destination = 'flaky'");
            Assert.Equal(before, after);
        }
        finally
        {
            cts.Cancel();
            await run;
        }
    }
}
