using System.Net;
using System.Text;
using System.Text.Json;
using EventPump.Api;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class ErrorAndQueryTests(PostgresFixture pg) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;
    private HttpClient _pub = null!;
    private HttpClient _int = null!;

    public async Task InitializeAsync()
    {
        _ds = await pg.CreateMigratedDatabaseAsync();
        var plan = TrackingPlan.Parse(
            """
            {"events":{
              "product_viewed": {"origin":"client","destinations":["ga4"]},
              "checkout_started": {"origin":"client","destinations":["ga4","amplitude"]},
              "first_visit": {"origin":"server","destinations":[]}}}
            """);
        await RegistrySync.SyncAsync(_ds, plan);
        _api = await ApiApp.StartAsync(new EpConfig
        {
            DbConnString = "unused-in-tests",
            Listen = "http://127.0.0.1:0",
            InternalListen = "http://127.0.0.1:0",
            ClientTokens = new() { ["tok-web"] = "webapp" },
            InternalToken = "internal-secret",
            ErrorRateLimitPermits = 3,
            ErrorRateLimitWindowSeconds = 60,
        }, _ds, plan, new MetricsRegistry());
        _pub = Client(_api.PublicBaseUri, "tok-web");
        _int = Client(_api.InternalBaseUri, null);
    }

    public async Task DisposeAsync()
    {
        _pub.Dispose();
        _int.Dispose();
        await _api.DisposeAsync();
    }

    private static HttpClient Client(Uri baseUri, string? bearer)
    {
        var client = new HttpClient(new SocketsHttpHandler { UseCookies = false }) { BaseAddress = baseUri };
        if (bearer is not null) client.DefaultRequestHeaders.Authorization = new("Bearer", bearer);
        return client;
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostError(string kind, string message, string stack = "at boom()")
        => _pub.PostAsync("/v1/errors", Json(
            $"{{\"kind\":\"{kind}\",\"message\":\"{message}\",\"stack\":\"{stack}\"," +
            $"\"anonymous_id\":\"{Guid.NewGuid()}\",\"app_version\":\"1.0\"," +
            "\"sdk\":{\"name\":\"event-pump-web\",\"version\":\"0.1.0\"}}"));

    private async Task PostEvent(string name, Guid anon, string? userId = null)
    {
        var user = userId is null ? "" : $",\"user_id\":\"{userId}\"";
        var response = await _pub.PostAsync("/v1/events", Json(
            $"{{\"events\":[{{\"event_id\":\"{Guid.NewGuid()}\",\"event_name\":\"{name}\"," +
            $"\"occurred_at\":\"{DateTimeOffset.UtcNow:O}\",\"anonymous_id\":\"{anon}\"{user}}}]}}"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------ /v1/errors

    [Fact]
    public async Task Errors_require_auth_and_dedupe_by_stack_hash()
    {
        using var anonymous = Client(_api.PublicBaseUri, null);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.PostAsync("/v1/errors", Json("{}"))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await PostError("TypeError", "x is null")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await PostError("TypeError", "x is null")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await PostError("RangeError", "overflow", "at other()")).StatusCode);

        Assert.Equal(2L, await Db.Scalar<long>(_ds, "SELECT count(*) FROM error_reports"));
        Assert.Equal(2, await Db.Scalar<int>(_ds,
            "SELECT occurrences FROM error_reports WHERE kind = 'TypeError'"));
        Assert.Equal("webapp", await Db.Scalar<string>(_ds,
            "SELECT app_id FROM error_reports WHERE kind = 'TypeError'"));
    }

    [Fact]
    public async Task Errors_truncate_oversized_fields()
    {
        var bigMessage = new string('m', 5_000);
        var bigStack = new string('s', 50_000);
        Assert.Equal(HttpStatusCode.NoContent,
            (await PostError("HugeError", bigMessage, bigStack)).StatusCode);

        Assert.True(await Db.Scalar<bool>(_ds,
            "SELECT length(message) <= 1024 AND length(stack) <= 8192 FROM error_reports"));
    }

    [Fact]
    public async Task Errors_have_their_own_rate_bucket()
    {
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await PostError("E", $"m{i}")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await PostError("E", "m3")).StatusCode);

        // an error storm must never throttle legitimate product events
        await PostEvent("product_viewed", Guid.NewGuid());
    }

    // ------------------------------------------------- /internal/v1/query

    [Fact]
    public async Task Query_filters_by_name_and_ids_with_delivery_join()
    {
        var anon = Guid.NewGuid();
        await PostEvent("product_viewed", anon, userId: "u-9");
        await PostEvent("checkout_started", Guid.NewGuid());

        using var byName = JsonDocument.Parse(await _int.GetStringAsync(
            "/internal/v1/query/events?event_name=product_viewed"));
        var rows = byName.RootElement.GetProperty("events");
        Assert.Equal(1, rows.GetArrayLength());
        var row = rows[0];
        Assert.Equal("product_viewed", row.GetProperty("event_name").GetString());
        Assert.Equal(anon.ToString(), row.GetProperty("anonymous_id").GetString());
        Assert.Equal("u-9", row.GetProperty("user_id").GetString());
        var deliveries = row.GetProperty("deliveries");
        Assert.Equal(1, deliveries.GetArrayLength());
        Assert.Equal("ga4", deliveries[0].GetProperty("destination").GetString());
        Assert.Equal("pending", deliveries[0].GetProperty("status").GetString());

        using var byAnon = JsonDocument.Parse(await _int.GetStringAsync(
            $"/internal/v1/query/events?anonymous_id={anon}"));
        Assert.Equal(1, byAnon.RootElement.GetProperty("events").GetArrayLength());

        using var byStatus = JsonDocument.Parse(await _int.GetStringAsync(
            "/internal/v1/query/events?status=dead"));
        Assert.Equal(0, byStatus.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Query_window_is_clamped_to_max_days()
    {
        await Db.Exec(_ds, "SELECT ep_ensure_partitions(current_date - 10)");
        await Db.Exec(_ds,
            """
            INSERT INTO events_outbox (event_id, event_name, origin, occurred_at, received_at)
            VALUES (gen_random_uuid(), 'product_viewed', 'client',
                    current_date - 10, (current_date - 10)::timestamptz + interval '1 hour')
            """);
        await PostEvent("product_viewed", Guid.NewGuid());

        using var response = JsonDocument.Parse(await _int.GetStringAsync(
            "/internal/v1/query/events?from=" + Uri.EscapeDataString(
                DateTimeOffset.UtcNow.AddDays(-30).ToString("O"))));
        // the 10-day-old row is outside the clamped window no matter what `from` says
        Assert.Equal(1, response.RootElement.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task Query_paginates_with_a_keyset_cursor()
    {
        for (var i = 0; i < 5; i++) await PostEvent("product_viewed", Guid.NewGuid());

        using var first = JsonDocument.Parse(await _int.GetStringAsync(
            "/internal/v1/query/events?limit=2"));
        Assert.Equal(2, first.RootElement.GetProperty("events").GetArrayLength());
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);

        using var second = JsonDocument.Parse(await _int.GetStringAsync(
            $"/internal/v1/query/events?limit=2&cursor={Uri.EscapeDataString(cursor!)}"));
        Assert.Equal(2, second.RootElement.GetProperty("events").GetArrayLength());

        var firstIds = first.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("event_id").GetString()).ToHashSet();
        var secondIds = second.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => e.GetProperty("event_id").GetString()).ToHashSet();
        Assert.Empty(firstIds.Intersect(secondIds));
    }

    [Fact]
    public async Task Query_identity_returns_the_session_row()
    {
        var session = Guid.NewGuid();
        var anon = Guid.NewGuid();
        var identity = await _pub.PostAsync("/v1/identity", Json(
            $"{{\"session_key\":\"{session}\",\"anonymous_id\":\"{anon}\",\"session_number\":2," +
            "\"user_id\":\"u-7\",\"handles\":{\"ga4_client_id\":\"1.2\"},\"context\":{\"language\":\"ar\"}}"));
        Assert.Equal(HttpStatusCode.NoContent, identity.StatusCode);

        using var row = JsonDocument.Parse(await _int.GetStringAsync(
            $"/internal/v1/query/identity/{session}"));
        Assert.Equal(anon.ToString(), row.RootElement.GetProperty("anonymous_id").GetString());
        Assert.Equal("u-7", row.RootElement.GetProperty("user_id").GetString());
        Assert.Equal("1.2", row.RootElement.GetProperty("ga4_client_id").GetString());
        Assert.Equal("ar", row.RootElement.GetProperty("context").GetProperty("language").GetString());

        Assert.Equal(HttpStatusCode.NotFound,
            (await _int.GetAsync($"/internal/v1/query/identity/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Query_endpoints_do_not_exist_on_the_public_listener()
    {
        Assert.Equal(HttpStatusCode.NotFound,
            (await _pub.GetAsync("/internal/v1/query/events")).StatusCode);
    }
}
