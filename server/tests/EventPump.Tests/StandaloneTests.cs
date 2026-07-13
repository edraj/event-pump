using System.Net.Http.Headers;
using System.Text;
using EventPump;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using EventPump.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class StandaloneTests(PostgresFixture pg)
{
    private sealed class FakeSender(string destination) : IDestinationSender
    {
        public string Destination => destination;

        public Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
            => Task.FromResult(SendResult.Delivered());
    }

    [Fact]
    public async Task One_process_ingests_delivers_and_exposes_unified_metrics()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        var plan = TrackingPlan.Parse(
            """{"events":{"product_viewed":{"origin":"client","destinations":["fake"]}}}""");
        await RegistrySync.SyncAsync(ds, plan);

        var config = new EpConfig
        {
            DbConnString = "unused-in-tests",
            Listen = "http://127.0.0.1:0",
            InternalListen = "http://127.0.0.1:0",
            ClientTokens = new() { ["tok-web"] = "webapp" },
            InternalToken = "internal-secret",
            WorkerPollMs = 50,
            BackoffBaseSeconds = 0,
            BackoffCapSeconds = 0,
        };

        var metrics = new MetricsRegistry();
        var host = await StandaloneHost.StartAsync(
            config, ds, plan, [new FakeSender("fake")], metrics, NullLoggerFactory.Instance);
        try
        {
            using var client = new HttpClient { BaseAddress = host.Api.PublicBaseUri };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "tok-web");
            var body = new StringContent(
                $"{{\"events\":[{{\"event_id\":\"{Guid.NewGuid()}\",\"event_name\":\"product_viewed\"," +
                $"\"occurred_at\":\"{DateTimeOffset.UtcNow:O}\",\"anonymous_id\":\"{Guid.NewGuid()}\"}}]}}",
                Encoding.UTF8, "application/json");
            var response = await client.PostAsync("/v1/events", body);
            Assert.True(response.IsSuccessStatusCode);

            // the in-process worker picks it up and delivers
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (await Db.Scalar<long>(ds,
                        "SELECT count(*) FROM events_delivery WHERE status = 'delivered'") == 1)
                    break;
                await Task.Delay(50);
            }
            Assert.Equal(1L, await Db.Scalar<long>(ds,
                "SELECT count(*) FROM events_delivery WHERE status = 'delivered'"));

            // one metrics surface carries both halves
            using var internalClient = new HttpClient { BaseAddress = host.Api.InternalBaseUri };
            var rendered = await internalClient.GetStringAsync("/metrics");
            Assert.Contains("""events_ingested_total{origin="client",endpoint="/v1/events"} 1""", rendered);
            Assert.Contains("""deliveries_total{destination="fake",status="delivered"} 1""", rendered);
        }
        finally
        {
            await host.StopAsync(); // graceful: worker drains, then the API stops
        }
    }
}
