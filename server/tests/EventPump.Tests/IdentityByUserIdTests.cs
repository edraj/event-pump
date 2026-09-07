using EventPump.Config;
using EventPump.Observability;
using EventPump.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// Person-scoped identity resolution for server-origin events (SPEC §12).
///
/// A backend producer knows the user_id and nothing else — it is not inside a
/// client session, so it has no session_key to send. These tests pin the rule
/// that such an event resolves identity by (app_id, user_id) against the rows
/// the SDK already wrote, and that a client-origin event never does.
/// </summary>
[Collection("pg")]
public class IdentityByUserIdTests(PostgresFixture pg)
{
    private static EpConfig Config(bool fallback = true) => new()
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
        IdentityUserFallback = fallback,
    };

    private sealed class CapturingSender(string destination, List<DeliveryItem> seen) : IDestinationSender
    {
        public string AppId => Db.DefaultAppId;
        public string Destination => destination;

        public Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct)
        {
            lock (seen) seen.Add(item);
            return Task.FromResult(SendResult.Delivered());
        }
    }

    /// <summary>An identity_registry row as the SDK writes it at S3 (SPEC §3).</summary>
    private static async Task WriteIdentity(
        NpgsqlDataSource ds, Guid sessionKey, Guid anonymousId, string? userId,
        string amplitudeDeviceId, string ga4ClientId, TimeSpan age,
        string? amplitudeUserId = null, string context = """{"os":"Android","model":"Galaxy A53","app_version":"3.2.1"}""")
    {
        await using var cmd = ds.CreateCommand(
            """
            INSERT INTO identity_registry (
                app_id, session_key, anonymous_id, user_id, session_number,
                amplitude_device_id, ga4_client_id, ga4_session_id,
                context, client_ip, amplitude_user_id, updated_at)
            VALUES ('zainmart', $1, $2, $3, 7, $4, $5, 'gsess-1',
                    $6::jsonb, '37.236.1.1', $7, now() - $8)
            """);
        cmd.Parameters.Add(new() { Value = sessionKey });
        cmd.Parameters.Add(new() { Value = anonymousId });
        cmd.Parameters.Add(new() { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = amplitudeDeviceId });
        cmd.Parameters.Add(new() { Value = ga4ClientId });
        cmd.Parameters.Add(new() { Value = context });
        cmd.Parameters.Add(new() { Value = (object?)amplitudeUserId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = age, NpgsqlDbType = NpgsqlDbType.Interval });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Emit(
        NpgsqlDataSource ds, string name, string? userId, Guid? sessionKey = null)
    {
        await using var cmd = ds.CreateCommand(
            """
            SELECT emit_event(
                p_app_id      => 'zainmart',
                p_event_name  => $1,
                p_user_id     => $2,
                p_session_key => $3,
                p_context     => '{"platform":"backend"}')
            """);
        cmd.Parameters.Add(new() { Value = name });
        cmd.Parameters.Add(new() { Value = (object?)userId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new() { Value = (object?)sessionKey ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<DeliveryItem>> Deliver(
        NpgsqlDataSource ds, EpConfig cfg, int expected = 1)
    {
        var seen = new List<DeliveryItem>();
        var worker = new DeliveryWorker(
            cfg, ds, [new CapturingSender("amplitude", seen)], new MetricsRegistry(),
            NullLoggerFactory.Instance);
        using var cts = new CancellationTokenSource();
        var run = worker.RunAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                lock (seen) if (seen.Count >= expected) break;
                await Task.Delay(50);
            }
        }
        finally
        {
            cts.Cancel();
            await run;
        }
        return seen;
    }

    [Fact]
    public async Task Server_event_with_only_a_user_id_resolves_the_persons_device_handles()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "update_order_status", "server", "amplitude");

        // The app session: anonymous at first, then setUser() stamps user_id
        // onto the same row (SPEC §3) — which is what makes this lookup work.
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            amplitudeDeviceId: "ABC-123", ga4ClientId: "GA-123", age: TimeSpan.FromHours(2),
            amplitudeUserId: "amp-e100c95f");

        await Emit(ds, "update_order_status", "e100c95f");

        var seen = await Deliver(ds, Config());
        var item = Assert.Single(seen);
        Assert.NotNull(item.Identity);
        Assert.True(item.Identity!.ResolvedByUserId);
        Assert.Equal("ABC-123", item.Identity.AmplitudeDeviceId);
        Assert.Equal("GA-123", item.Identity.Ga4ClientId);
        Assert.Equal("amp-e100c95f", item.Identity.AmplitudeUserId);
        Assert.Equal("e100c95f", item.Identity.UserId);
    }

    [Fact]
    public async Task Person_resolved_identity_carries_no_session_or_device_context()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "update_order_status", "server", "amplitude");
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "ABC-123", "GA-123", TimeSpan.FromHours(2));

        await Emit(ds, "update_order_status", "e100c95f");

        var item = Assert.Single(await Deliver(ds, Config()));
        // The backend event did not happen on that phone, in that session, at
        // that IP — so none of it rides along (see IdentitySnapshot).
        Assert.Equal("{}", item.Identity!.ContextJson);
        Assert.Null(item.Identity.ClientIp);
        Assert.Null(item.Identity.Ga4SessionId);
        Assert.Null(item.Identity.SessionNumber);
        // The event keeps the context the producer actually sent.
        Assert.Contains("backend", item.ContextJson);
    }

    [Fact]
    public async Task Picks_the_most_recently_active_session_of_that_person()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "update_order_status", "server", "amplitude");
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "OLD-PHONE", "GA-OLD", TimeSpan.FromDays(20));
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "NEW-PHONE", "GA-NEW", TimeSpan.FromHours(1));

        await Emit(ds, "update_order_status", "e100c95f");

        var item = Assert.Single(await Deliver(ds, Config()));
        Assert.Equal("NEW-PHONE", item.Identity!.AmplitudeDeviceId);
    }

    /// <summary>
    /// The lookup itself is unbounded by age: an old amplitude_device_id or
    /// ga4_client_id still names the right person, and the event carries
    /// user_id besides. Only Adjust cares how old a handle is, and it makes
    /// that call itself (see SenderTests). What the worker owes a sender is
    /// the row plus its age.
    /// </summary>
    [Fact]
    public async Task Returns_an_old_identity_row_and_reports_its_age()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "update_order_status", "server", "amplitude");
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "OLD-PHONE", "GA-OLD", TimeSpan.FromDays(400));

        await Emit(ds, "update_order_status", "e100c95f");

        var item = Assert.Single(await Deliver(ds, Config()));
        Assert.Equal("OLD-PHONE", item.Identity!.AmplitudeDeviceId);
        Assert.NotNull(item.Identity.UpdatedAt);
        Assert.InRange((DateTime.UtcNow - item.Identity.UpdatedAt!.Value).TotalDays, 399, 401);
    }

    [Fact]
    public async Task Fallback_disabled_restores_session_key_only_resolution()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "update_order_status", "server", "amplitude");
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "ABC-123", "GA-123", TimeSpan.FromHours(1));

        await Emit(ds, "update_order_status", "e100c95f");

        var item = Assert.Single(await Deliver(ds, Config(fallback: false)));
        Assert.Null(item.Identity);
    }

    [Fact]
    public async Task Never_matches_a_row_belonging_to_a_different_person()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "update_order_status", "server", "amplitude");
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "someone-else",
            "OTHER-PHONE", "GA-OTHER", TimeSpan.FromHours(1));
        // A never-logged-in session carries user_id NULL and must stay unreachable.
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), null,
            "ANON-PHONE", "GA-ANON", TimeSpan.FromMinutes(5));

        await Emit(ds, "update_order_status", "e100c95f");

        var item = Assert.Single(await Deliver(ds, Config()));
        Assert.Null(item.Identity);
    }

    [Fact]
    public async Task Server_event_without_a_user_id_resolves_nothing()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "update_order_status", "server", "amplitude");
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "ABC-123", "GA-123", TimeSpan.FromHours(1));

        await Emit(ds, "update_order_status", userId: null);

        var item = Assert.Single(await Deliver(ds, Config()));
        Assert.Null(item.Identity);
    }

    [Fact]
    public async Task Client_event_still_resolves_by_session_key_and_keeps_its_context()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "screen_viewed", "server", "amplitude");
        var session = Guid.NewGuid();
        // A newer session of the same person: proof the session_key join wins.
        await WriteIdentity(ds, session, Guid.NewGuid(), "e100c95f",
            "THIS-SESSION", "GA-THIS", TimeSpan.FromHours(3));
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "NEWER-SESSION", "GA-NEWER", TimeSpan.FromMinutes(1));

        await Emit(ds, "screen_viewed", "e100c95f", session);

        var item = Assert.Single(await Deliver(ds, Config()));
        Assert.False(item.Identity!.ResolvedByUserId);
        Assert.Equal("THIS-SESSION", item.Identity.AmplitudeDeviceId);
        // A real session join keeps everything, unlike the person fallback.
        Assert.Equal("gsess-1", item.Identity.Ga4SessionId);
        Assert.Equal(7, item.Identity.SessionNumber);
        Assert.Equal("37.236.1.1", item.Identity.ClientIp);
        Assert.Contains("Galaxy A53", item.Identity.ContextJson);
    }

    [Fact]
    public async Task An_event_naming_a_session_that_has_no_row_does_not_borrow_the_persons_other_device()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "screen_viewed", "server", "amplitude");
        // The identity POST for this session has not landed yet. The NoIdentity
        // grace window is what waits for it; silently substituting the person's
        // other phone would attribute the event to the wrong device forever.
        await WriteIdentity(ds, Guid.NewGuid(), Guid.NewGuid(), "e100c95f",
            "OTHER-PHONE", "GA-OTHER", TimeSpan.FromMinutes(5));

        await Emit(ds, "screen_viewed", "e100c95f", Guid.NewGuid());

        var item = Assert.Single(await Deliver(ds, Config()));
        Assert.Null(item.Identity);
    }
}
