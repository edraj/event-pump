using System.Net;
using System.Text;
using System.Text.Json;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using EventPump.Senders;
using EventPump.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// The Flutter SDK's moengageDeviceCustomerId mode, end to end through the
/// worker and the real MoEngage sender: the pre-login registration itself
/// carries moengage_customer_id = anonymous_id, so the first_visit that same
/// registration emits — the event a separate, later identify() call could
/// never reliably reach — is delivered under the device id rather than
/// skipped `no_user_id`.
/// </summary>
[Collection("pg")]
public class MoEngageDeviceCustomerIdTests(PostgresFixture pg)
{
    private static EpConfig Config() => new()
    {
        DbConnString = "unused-in-tests",
        WorkerPollMs = 50,
        ClaimBatchSize = 10,
        SendConcurrency = 1,
        BackoffBaseSeconds = 0,
        BackoffCapSeconds = 0,
        MaxAttempts = 10,
        BreakerThreshold = 100,
        BreakerPauseSeconds = 60,
        LeaseSeconds = 300,
        IdentityGraceSeconds = 0,
        MoEngageEnabled = true,
        MoEngageAppId = "MOE-APP",
        MoEngageApiKey = "moe-key",
    };

    private static readonly TrackingPlan Plan = TrackingPlan.Parse(
        """
        { "events": { "first_visit": { "origin": "server", "destinations": ["moengage"] } } }
        """);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Bodies) Bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"success"}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static async Task<List<string>> DeliverToMoEngage(NpgsqlDataSource ds)
    {
        var handler = new CapturingHandler();
        var tenant = TenantFactory.From(Config(), Plan);
        var worker = new DeliveryWorker(
            Config(), ds, [new MoEngageSender(tenant, TenantFactory.TimeoutMs, handler)],
            new MetricsRegistry(), NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (handler.Bodies) if (handler.Bodies.Count > 0) break;
                if (await Db.Scalar<long>(ds,
                        "SELECT count(*) FROM events_delivery WHERE status NOT IN ('pending', 'failed')") > 0)
                    break;
                await Task.Delay(50);
            }
        }
        finally
        {
            cts.Cancel();
            await run;
        }
        lock (handler.Bodies) return [.. handler.Bodies];
    }

    private static EventStore.IdentityUpsert Registration(
        Guid session, Guid anon, string? userId, string? moengage) => new(
            SessionKey: session, AnonymousId: anon, SessionNumber: 1, UserId: userId,
            Ga4ClientId: anon.ToString(), Ga4SessionId: null, FirebaseAppInstanceId: null,
            AmplitudeDeviceId: anon.ToString(), AdjustAdid: null, AdjustPlatformAdId: null,
            Fbp: null, Fbc: null, ClickIdsJson: null,
            ContextJson: """{"os":"Android","model":"Pixel 8"}""",
            MoEngageCustomerId: moengage);

    [Fact]
    public async Task First_visit_of_a_pre_login_device_reaches_moengage_under_the_device_id()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "first_visit", "server", "moengage");
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();

        Assert.True(await EventStore.UpsertIdentityAsync(
            ds, Db.DefaultAppId, Registration(session, anon, userId: null, moengage: anon.ToString()),
            clientIp: null, CancellationToken.None));

        var body = Assert.Single(await DeliverToMoEngage(ds));
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(anon.ToString(), payload.RootElement.GetProperty("customer_id").GetString());
        var action = payload.RootElement.GetProperty("actions")[0];
        Assert.Equal("first_visit", action.GetProperty("action").GetString());
        // emit_event stores no platform; the session's os supplies MoEngage's.
        Assert.Equal("ANDROID", action.GetProperty("platform").GetString());
    }

    [Fact]
    public async Task Without_the_device_id_a_pre_login_first_visit_is_still_skipped()
    {
        // The mode is opt-in: a tenant whose SDK does not stamp the device id
        // keeps today's behaviour rather than gaining a MoEngage user per
        // device.
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "first_visit", "server", "moengage");

        await EventStore.UpsertIdentityAsync(
            ds, Db.DefaultAppId, Registration(Guid.NewGuid(), Guid.NewGuid(), userId: null, moengage: null),
            clientIp: null, CancellationToken.None);

        Assert.Empty(await DeliverToMoEngage(ds));
        Assert.Equal("no_user_id", await Db.Scalar<string>(ds,
            "SELECT last_error FROM events_delivery WHERE destination = 'moengage'"));
    }
}
