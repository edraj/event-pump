using EventPump.Config;
using EventPump.Data;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class ErasureStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    private const int Retention = 30;

    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync()
    {
        _ds = await pg.CreateMigratedDatabaseAsync();
        await RegistrySync.SyncTenantAsync(_ds, "zainmart", TrackingPlan.Parse(
            """{ "events": { "product_viewed": { "origin": "client", "destinations": ["moengage"] } } }"""));
    }

    public Task DisposeAsync() => _ds.DisposeAsync().AsTask();

    private async Task Identity(
        string? userId, string? moengage = null, string? adjust = null,
        string? amplitude = null, string? ga4Client = null, string updatedAt = "now()",
        string appId = "zainmart")
    {
        await using var cmd = _ds.CreateCommand(
            $"""
            INSERT INTO identity_registry
                (session_key, anonymous_id, app_id, user_id,
                 moengage_customer_id, adjust_adid, amplitude_user_id,
                 ga4_client_id, updated_at)
            VALUES (gen_random_uuid(), gen_random_uuid(), $1, $2, $3, $4, $5, $6, {updatedAt})
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(Str(userId));
        cmd.Parameters.Add(Str(moengage));
        cmd.Parameters.Add(Str(adjust));
        cmd.Parameters.Add(Str(amplitude));
        cmd.Parameters.Add(Str(ga4Client));
        await cmd.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter Str(string? v) =>
        new() { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)v ?? DBNull.Value };

    private async Task<Guid> SeedDelivery(
        string userId, string destination, string status, string appId = "zainmart")
    {
        var eventId = Guid.NewGuid();
        await Db.RegisterEventForApp(_ds, appId, "product_viewed", "server", destination);
        await Db.EmitForApp(_ds, appId, "product_viewed", eventId);
        await using var cmd = _ds.CreateCommand(
            """
            WITH o AS (
                UPDATE events_outbox SET user_id = $2
                 WHERE event_id = $1 RETURNING id, received_at
            )
            UPDATE events_delivery d SET status = $3
              FROM o WHERE d.event_ref = o.id AND d.received_at = o.received_at
            """);
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = eventId });
        cmd.Parameters.Add(new() { Value = userId });
        cmd.Parameters.Add(new() { Value = status });
        await cmd.ExecuteNonQueryAsync();
        return eventId;
    }

    private Task<string> StatusOf(Guid eventId) => Db.Scalar<string>(_ds,
        $"""
         SELECT d.status FROM events_delivery d
         JOIN events_outbox o ON o.id = d.event_ref AND o.received_at = d.received_at
         WHERE o.event_id = '{eventId}'
         """);

    private Task<int> OutboxCount(string eventName) => Db.Scalar<long>(_ds,
        $"SELECT count(*) FROM events_outbox WHERE event_name = '{eventName}'")
        .ContinueWith(t => (int)t.Result);

    private Task<string> ContextOf(string eventName) => Db.Scalar<string>(_ds,
        $"SELECT context::text FROM events_outbox WHERE event_name = '{eventName}' LIMIT 1");

    [Fact]
    public async Task Resolves_nothing_for_a_person_we_have_no_sessions_for()
    {
        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "ghost", default);

        Assert.Null(handles.MoEngageCustomerId);
        Assert.Null(handles.AdjustAdid);
        Assert.Equal("{}", handles.ToContextJson());
    }

    [Fact]
    public async Task Resolves_every_handle_recorded_for_the_person()
    {
        await Identity("u-1", moengage: "M-1", adjust: "A-1", amplitude: "AM-1", ga4Client: "G-1");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal("M-1", handles.MoEngageCustomerId);
        Assert.Equal("A-1", handles.AdjustAdid);
        Assert.Equal("AM-1", handles.AmplitudeUserId);
        Assert.Equal("G-1", handles.Ga4ClientId);
    }

    [Fact]
    public async Task Takes_the_most_recent_non_null_per_handle_across_sessions()
    {
        await Identity("u-1", moengage: "M-old", adjust: "A-only",
                       updatedAt: "now() - interval '2 days'");
        await Identity("u-1", moengage: "M-new");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal("M-new", handles.MoEngageCustomerId);
        Assert.Equal("A-only", handles.AdjustAdid);
    }

    [Fact]
    public async Task Another_tenants_session_never_supplies_a_handle()
    {
        await Identity("u-1", moengage: "OTHER", appId: "other");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        Assert.Null(handles.MoEngageCustomerId);
    }

    [Fact]
    public void Context_json_omits_handles_we_do_not_have()
    {
        var handles = new EventStore.ErasureHandles("M-1", null, null, null, null, "G-1", null);

        Assert.Equal("""{"moengage_customer_id":"M-1","ga4_client_id":"G-1"}""", handles.ToContextJson());
    }

    [Fact]
    public async Task Cancels_pending_and_failed_deliveries_for_that_person()
    {
        var pending = await SeedDelivery("u-1", "moengage", "pending");
        var failed = await SeedDelivery("u-1", "moengage", "failed");

        var cancelled = await EventStore.CancelPendingDeliveriesAsync(
            _ds, "zainmart", "u-1", ["moengage"], Retention, default);

        Assert.Equal(2, cancelled);
        Assert.Equal("skipped", await StatusOf(pending));
        Assert.Equal("skipped", await StatusOf(failed));
    }

    [Fact]
    public async Task Leaves_an_already_delivered_row_alone()
    {
        var delivered = await SeedDelivery("u-1", "moengage", "delivered");

        var cancelled = await EventStore.CancelPendingDeliveriesAsync(
            _ds, "zainmart", "u-1", ["moengage"], Retention, default);

        Assert.Equal(0, cancelled);
        Assert.Equal("delivered", await StatusOf(delivered));
    }

    [Fact]
    public async Task Leaves_destinations_outside_the_list_alone()
    {
        var other = await SeedDelivery("u-1", "ga4", "pending");

        var cancelled = await EventStore.CancelPendingDeliveriesAsync(
            _ds, "zainmart", "u-1", ["moengage"], Retention, default);

        Assert.Equal(0, cancelled);
        Assert.Equal("pending", await StatusOf(other));
    }

    [Fact]
    public async Task Leaves_another_persons_deliveries_alone()
    {
        var other = await SeedDelivery("u-2", "moengage", "pending");

        var cancelled = await EventStore.CancelPendingDeliveriesAsync(
            _ds, "zainmart", "u-1", ["moengage"], Retention, default);

        Assert.Equal(0, cancelled);
        Assert.Equal("pending", await StatusOf(other));
    }

    [Fact]
    public async Task Queues_one_delivery_row_per_destination_from_a_single_outbox_row()
    {
        var (_, queued) = await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure", "adjust_erasure"], "{}", Retention, default);

        Assert.Equal(["adjust_erasure", "moengage_erasure"], queued.Order());
        Assert.Equal(1, await OutboxCount(TrackingPlan.ErasureRequestedEventName));
    }

    [Fact]
    public async Task Stashes_the_resolved_handles_on_the_outbox_row()
    {
        await Identity("u-1", moengage: "M-1", adjust: "A-1");
        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure"], handles.ToContextJson(), Retention, default);

        var context = await ContextOf(TrackingPlan.ErasureRequestedEventName);
        Assert.Contains("\"moengage_customer_id\": \"M-1\"", context);
        Assert.Contains("\"adjust_adid\": \"A-1\"", context);
    }

    [Fact]
    public async Task Queues_nothing_when_the_tenant_has_no_erasure_destinations()
    {
        var (_, queued) = await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            [], "{}", Retention, default);

        Assert.Empty(queued);
        Assert.Equal(0, await OutboxCount(TrackingPlan.ErasureRequestedEventName));
    }

    [Fact]
    public async Task Does_not_queue_a_second_erasure_while_one_is_in_flight()
    {
        await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure"], "{}", Retention, default);

        var (_, again) = await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure"], "{}", Retention, default);

        Assert.Empty(again);
        Assert.Equal(1, await OutboxCount(TrackingPlan.ErasureRequestedEventName));
    }

    [Fact]
    public async Task Re_drives_only_the_destination_that_is_not_in_flight()
    {
        await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure"], "{}", Retention, default);

        var (_, again) = await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure", "adjust_erasure"], "{}", Retention, default);

        Assert.Equal(["adjust_erasure"], again);
    }

    [Fact]
    public async Task The_two_erasure_variants_do_not_deduplicate_against_each_other()
    {
        await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure"], "{}", Retention, default);

        var (_, attributes) = await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.AttributesErasureRequestedEventName,
            ["moengage_erasure"], "{}", Retention, default);

        Assert.Equal(["moengage_erasure"], attributes);
    }

    [Fact]
    public async Task Another_persons_in_flight_erasure_does_not_block_this_one()
    {
        await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-2", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure"], "{}", Retention, default);

        var (_, queued) = await EventStore.EnqueueErasureAsync(
            _ds, "zainmart", "u-1", TrackingPlan.ErasureRequestedEventName,
            ["moengage_erasure"], "{}", Retention, default);

        Assert.Equal(["moengage_erasure"], queued);
    }
}
