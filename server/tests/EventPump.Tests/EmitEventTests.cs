using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class EmitEventTests(PostgresFixture pg)
{
    [Fact]
    public async Task Fans_out_delivery_rows_per_routing_map()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "order_placed", "server", "amplitude", "ga4");

        var eventId = await Db.Emit(ds, "order_placed");

        Assert.NotNull(eventId);
        Assert.Equal(1L, await Db.Scalar<long>(ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{eventId}' AND origin = 'server'"));
        Assert.Equal(["amplitude", "ga4"], await Db.DeliveryDestinations(ds, eventId.Value));
        Assert.Equal(2L, await Db.Scalar<long>(ds,
            "SELECT count(*) FROM events_delivery WHERE status = 'pending' AND attempts = 0 AND next_attempt_at <= now()"));
    }

    [Fact]
    public async Task Event_with_no_routed_destinations_gets_no_delivery_rows()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "internal_only_thing", "server");

        var eventId = await Db.Emit(ds, "internal_only_thing");

        Assert.NotNull(eventId);
        Assert.Equal(1L, await Db.Scalar<long>(ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{eventId}'"));
        Assert.Empty(await Db.DeliveryDestinations(ds, eventId.Value));
    }

    [Fact]
    public async Task Unknown_event_name_raises()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Assert.ThrowsAsync<PostgresException>(() => Db.Emit(ds, "never_registered"));
    }

    [Fact]
    public async Task Client_origin_name_raises_on_sql_path()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "product_viewed", "client", "ga4");
        await Assert.ThrowsAsync<PostgresException>(() => Db.Emit(ds, "product_viewed"));
    }

    [Fact]
    public async Task Duplicate_event_id_is_a_noop_returning_null()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "order_placed", "server", "ga4");
        var explicitId = Guid.NewGuid();

        var first = await Db.Emit(ds, "order_placed", eventId: explicitId);
        var second = await Db.Emit(ds, "order_placed", eventId: explicitId);

        Assert.Equal(explicitId, first);
        Assert.Null(second);
        Assert.Equal(1L, await Db.Scalar<long>(ds,
            $"SELECT count(*) FROM events_outbox WHERE event_id = '{explicitId}'"));
        Assert.Equal(1L, await Db.Scalar<long>(ds, "SELECT count(*) FROM events_delivery"));
    }

    [Fact]
    public async Task First_visit_emitted_once_ever_per_anonymous_id()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "session_started", "server", "amplitude");
        await Db.Exec(ds, "UPDATE event_registry SET destinations = '{moengage}' WHERE event_name = 'first_visit'");
        var anon = Guid.NewGuid();

        await Db.Emit(ds, "session_started", anonymousId: anon);
        await Db.Emit(ds, "session_started", anonymousId: anon);

        Assert.Equal(1L, await Db.Scalar<long>(ds,
            $"SELECT count(*) FROM first_seen WHERE anonymous_id = '{anon}'"));
        Assert.Equal(1L, await Db.Scalar<long>(ds,
            $"SELECT count(*) FROM events_outbox WHERE event_name = 'first_visit' AND anonymous_id = '{anon}'"));

        // routed to moengage only — never GA4/Amplitude (SPEC §8)
        var firstVisitId = await Db.Scalar<Guid>(ds,
            $"SELECT event_id FROM events_outbox WHERE event_name = 'first_visit' AND anonymous_id = '{anon}'");
        Assert.Equal(["moengage"], await Db.DeliveryDestinations(ds, firstVisitId));
    }

    [Fact]
    public async Task Emit_event_rejects_reserved_names()
    {
        // SPEC §6.1: reserved server-origin events (e.g. ep_attributes_synced) are
        // auto-registered and must not be emittable by producers.
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.Exec(ds,
            "INSERT INTO event_registry (event_name, origin, destinations, reserved) VALUES ('ep_attributes_synced', 'server', '{moengage_customer}', true)");

        var error = await Assert.ThrowsAsync<PostgresException>(() => Db.Emit(ds, "ep_attributes_synced"));
        Assert.Contains("reserved event_name", error.MessageText);
    }

    [Fact]
    public async Task No_first_visit_without_anonymous_id()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "batch_job_ran", "server", "amplitude");

        await Db.Emit(ds, "batch_job_ran");

        Assert.Equal(0L, await Db.Scalar<long>(ds, "SELECT count(*) FROM first_seen"));
        Assert.Equal(0L, await Db.Scalar<long>(ds,
            "SELECT count(*) FROM events_outbox WHERE event_name = 'first_visit'"));
    }
}
