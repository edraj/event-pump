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
        string appId = "zainmart", Guid? anonymousId = null, string? os = null,
        string? platformAdId = null, string? amplitudeDevice = null)
    {
        await using var cmd = _ds.CreateCommand(
            $"""
            INSERT INTO identity_registry
                (session_key, anonymous_id, app_id, user_id,
                 moengage_customer_id, adjust_adid, amplitude_user_id,
                 ga4_client_id, adjust_platform_ad_id, amplitude_device_id,
                 context, updated_at)
            VALUES (gen_random_uuid(), $7, $1, $2, $3, $4, $5, $6, $9, $10,
                    coalesce($8::jsonb, jsonb_build_object()), {updatedAt})
            """);
        cmd.Parameters.Add(new() { Value = appId });
        cmd.Parameters.Add(Str(userId));
        cmd.Parameters.Add(Str(moengage));
        cmd.Parameters.Add(Str(adjust));
        cmd.Parameters.Add(Str(amplitude));
        cmd.Parameters.Add(Str(ga4Client));
        cmd.Parameters.Add(new()
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = (object?)(anonymousId ?? Guid.NewGuid()),
        });
        cmd.Parameters.Add(Str(os is null ? null : $$"""{"os":"{{os}}"}"""));
        cmd.Parameters.Add(Str(platformAdId));
        cmd.Parameters.Add(Str(amplitudeDevice));
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

    // A pre-login event: no user_id, only the device's anonymous_id.
    private async Task<Guid> SeedAnonymousDelivery(
        Guid anonymousId, string destination, string status)
    {
        var eventId = Guid.NewGuid();
        await Db.RegisterEventForApp(_ds, "zainmart", "product_viewed", "server", destination);
        await Db.EmitForApp(_ds, "zainmart", "product_viewed", eventId);
        await using var cmd = _ds.CreateCommand(
            """
            WITH o AS (
                UPDATE events_outbox SET user_id = NULL, anonymous_id = $2
                 WHERE event_id = $1 RETURNING id, received_at
            )
            UPDATE events_delivery d SET status = $3
              FROM o WHERE d.event_ref = o.id AND d.received_at = o.received_at
            """);
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = eventId });
        cmd.Parameters.Add(new() { NpgsqlDbType = NpgsqlDbType.Uuid, Value = anonymousId });
        cmd.Parameters.Add(new() { Value = status });
        await cmd.ExecuteNonQueryAsync();
        return eventId;
    }

    private Task<long> IdentityRows(string where) => Db.Scalar<long>(_ds,
        $"SELECT count(*) FROM identity_registry WHERE app_id = 'zainmart' AND {where}");

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
        Assert.Empty(handles.Adjust);
        Assert.Equal("{}", handles.ToContextJson());
    }

    [Fact]
    public async Task Resolves_every_handle_recorded_for_the_person()
    {
        await Identity("u-1", moengage: "M-1", adjust: "A-1", amplitude: "AM-1", ga4Client: "G-1");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal("M-1", handles.MoEngageCustomerId);
        Assert.Equal("A-1", Assert.Single(handles.Adjust).Adid);
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
        Assert.Equal("A-only", Assert.Single(handles.Adjust).Adid);
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
        var handles = new EventStore.ErasureHandles(
            MoEngageCustomerId: "M-1", Ga4ClientId: "G-1");

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
        Assert.Contains("\"adid\": \"A-1\"", context);
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

    [Fact]
    public async Task Resolves_the_os_that_says_which_parameter_carries_the_ad_id()
    {
        await Identity("u-1", platformAdId: "RAW-7", os: "android");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        var device = Assert.Single(handles.Adjust);
        Assert.Equal("RAW-7", device.PlatformAdId);
        Assert.Equal("android", device.Os);
    }

    [Fact]
    public async Task Reads_the_os_off_the_session_that_supplied_the_ad_id()
    {
        // The Android phone recorded a GAID; the later iPhone session recorded
        // no ad id at all (ATT denied). Taking the newest os per column pairs
        // that GAID with "ios", and AdjustErasureSender then sends a GAID as
        // `idfa` — Adjust matches nothing, answers 200, and the delivery reads
        // `delivered` with the device never forgotten.
        await Identity("u-1", platformAdId: "GAID-1", os: "android",
                       updatedAt: "now() - interval '2 days'");
        await Identity("u-1", os: "ios");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        var device = Assert.Single(handles.Adjust);
        Assert.Equal("GAID-1", device.PlatformAdId);
        Assert.Equal("android", device.Os);
    }

    [Fact]
    public async Task Resolves_every_device_the_person_was_seen_on()
    {
        // Adjust forgets one device per call and Amplitude keeps events per
        // device: keeping only the newest leaves the old phone tracked while
        // the audit trail reports both destinations complete.
        await Identity("u-1", adjust: "ADID-old", amplitudeDevice: "AMP-old",
                       updatedAt: "now() - interval '2 days'");
        await Identity("u-1", adjust: "ADID-new", amplitudeDevice: "AMP-new");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal(["ADID-new", "ADID-old"], handles.Adjust.Select(d => d.Adid).Order());
        Assert.Equal(["AMP-new", "AMP-old"], handles.AmplitudeDevices.Order());
    }

    [Fact]
    public async Task Counts_one_device_once_however_many_sessions_recorded_it()
    {
        await Identity("u-1", adjust: "ADID-1", amplitudeDevice: "AMP-1",
                       updatedAt: "now() - interval '2 days'");
        await Identity("u-1", adjust: "ADID-1", amplitudeDevice: "AMP-1");

        var handles = await EventStore.ResolveErasureHandlesAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal("ADID-1", Assert.Single(handles.Adjust).Adid);
        Assert.Equal("AMP-1", Assert.Single(handles.AmplitudeDevices));
    }

    [Fact]
    public void An_os_on_its_own_is_not_a_handle_we_can_erase_under()
    {
        var handles = new EventStore.ErasureHandles(
            AdjustDevices: [new EventStore.AdjustDevice(null, null, "ios")]);

        Assert.False(handles.HasAnyHandle);
    }

    [Fact]
    public void Round_trips_every_device_through_the_context_json()
    {
        var handles = new EventStore.ErasureHandles(
            MoEngageCustomerId: "M-1",
            AdjustDevices: [
                new EventStore.AdjustDevice("ADID-1", null, null),
                new EventStore.AdjustDevice(null, "RAW-7", "ios")],
            AmplitudeDeviceIds: ["AMP-1", "AMP-2"]);

        var parsed = EventStore.ErasureHandles.FromContextJson(handles.ToContextJson());

        Assert.Equal("M-1", parsed.MoEngageCustomerId);
        Assert.Equal(["ADID-1", null], parsed.Adjust.Select(d => d.Adid));
        Assert.Equal("ios", parsed.Adjust[1].Os);
        Assert.Equal(["AMP-1", "AMP-2"], parsed.AmplitudeDevices);
    }

    [Fact]
    public void Reads_the_single_device_shape_written_before_the_fan_out()
    {
        // An erasure queued or audited by an older build carries scalar keys.
        // Dropping them would lose the only handle that names the person.
        var parsed = EventStore.ErasureHandles.FromContextJson(
            """
            {"moengage_customer_id":"M-1","adjust_platform_ad_id":"RAW-7",
             "os":"android","amplitude_device_id":"AMP-1"}
            """);

        Assert.Equal("M-1", parsed.MoEngageCustomerId);
        var device = Assert.Single(parsed.Adjust);
        Assert.Equal("RAW-7", device.PlatformAdId);
        Assert.Equal("android", device.Os);
        Assert.Equal("AMP-1", Assert.Single(parsed.AmplitudeDevices));
    }

    [Fact]
    public async Task Cancels_the_persons_pre_login_deliveries_too()
    {
        var device = Guid.NewGuid();
        await Identity("u-1", anonymousId: device);
        var beforeLogin = await SeedAnonymousDelivery(device, "moengage", "pending");

        var cancelled = await EventStore.CancelPendingDeliveriesAsync(
            _ds, "zainmart", "u-1", ["moengage"], Retention, default);

        // Left pending it would rebuild, from the same device's ad id, exactly
        // the profile the downstream delete had just removed.
        Assert.Equal(1, cancelled);
        Assert.Equal("skipped", await StatusOf(beforeLogin));
    }

    [Fact]
    public async Task Leaves_a_device_this_person_never_used_alone()
    {
        await Identity("u-1", anonymousId: Guid.NewGuid());
        var stranger = await SeedAnonymousDelivery(Guid.NewGuid(), "moengage", "pending");

        var cancelled = await EventStore.CancelPendingDeliveriesAsync(
            _ds, "zainmart", "u-1", ["moengage"], Retention, default);

        Assert.Equal(0, cancelled);
        Assert.Equal("pending", await StatusOf(stranger));
    }

    [Fact]
    public async Task Deletes_the_persons_pre_login_identity_rows_too()
    {
        var device = Guid.NewGuid();
        await Identity("u-1", anonymousId: device);
        await Identity(null, anonymousId: device);

        var deleted = await EventStore.DeleteIdentityRegistryAsync(
            _ds, "zainmart", "u-1", default);

        // The anonymous row holds the same ADID, device id, IP and location,
        // and nothing ages this table out — filtering on user_id alone would
        // keep it forever.
        Assert.Equal(2, deleted);
        Assert.Equal(0, await IdentityRows($"anonymous_id = '{device}'"));
    }

    [Fact]
    public async Task Leaves_the_other_account_on_a_shared_device_alone()
    {
        var device = Guid.NewGuid();
        await Identity("u-1", anonymousId: device);
        await Identity("u-2", anonymousId: device);

        await EventStore.DeleteIdentityRegistryAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal(1, await IdentityRows("user_id = 'u-2'"));
    }

    [Fact]
    public async Task Leaves_a_shared_browsers_unclaimed_rows_to_the_other_account()
    {
        // `ep_aid` is per browser, not per person. Once a second account has
        // logged in on it, an unclaimed row under that id is as likely to be
        // their pre-login session as this person's, and erasing it would
        // delete a second person's data on the first person's request.
        var browser = Guid.NewGuid();
        await Identity("u-1", anonymousId: browser);
        await Identity("u-2", anonymousId: browser);
        await Identity(null, anonymousId: browser);

        await EventStore.DeleteIdentityRegistryAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal(1, await IdentityRows("user_id IS NULL"));
    }

    [Fact]
    public async Task Leaves_a_shared_browsers_anonymous_deliveries_alone()
    {
        var browser = Guid.NewGuid();
        await Identity("u-1", anonymousId: browser);
        await Identity("u-2", anonymousId: browser);
        var theirs = await SeedAnonymousDelivery(browser, "moengage", "pending");

        var cancelled = await EventStore.CancelPendingDeliveriesAsync(
            _ds, "zainmart", "u-1", ["moengage"], Retention, default);

        Assert.Equal(0, cancelled);
        Assert.Equal("pending", await StatusOf(theirs));
    }

    [Fact]
    public async Task Another_tenants_anonymous_rows_survive_this_tenants_erasure()
    {
        var device = Guid.NewGuid();
        await Identity("u-1", anonymousId: device);
        await Identity(null, anonymousId: device, appId: "other");

        await EventStore.DeleteIdentityRegistryAsync(_ds, "zainmart", "u-1", default);

        Assert.Equal(1, await Db.Scalar<long>(_ds,
            $"SELECT count(*) FROM identity_registry WHERE app_id = 'other'"));
    }
}
