using EventPump.Worker;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class PartitionMaintenanceTests(PostgresFixture pg)
{
    [Fact]
    public async Task Creates_ahead_drops_expired_and_prunes_dedupe()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        var oldSuffix = await Db.Scalar<string>(ds, "SELECT to_char(current_date - 40, 'YYYYMMDD')");
        var aheadSuffix = await Db.Scalar<string>(ds, "SELECT to_char(current_date + 3, 'YYYYMMDD')");

        await Db.Exec(ds, "SELECT ep_ensure_partitions(current_date - 40)");
        await Db.Exec(ds,
            """
            INSERT INTO events_outbox (event_id, event_name, origin, occurred_at, received_at)
            VALUES (gen_random_uuid(), 'old_event', 'server',
                    current_date - 40, (current_date - 40)::timestamptz + interval '1 hour')
            """);
        await Db.Exec(ds,
            """
            INSERT INTO events_delivery (event_ref, received_at, destination, status)
            SELECT id, received_at, 'ga4', 'delivered' FROM events_outbox
            WHERE received_at < current_date - 39
            """);
        await Db.Exec(ds,
            "INSERT INTO events_dedupe (event_id, received_at) VALUES (gen_random_uuid(), now() - interval '40 days')");

        await PartitionMaintenance.RunOnceAsync(ds, retentionDays: 30, retentionDeadDays: 90, aheadDays: 3);

        Assert.True(await Db.Scalar<bool>(ds, $"SELECT to_regclass('events_outbox_{oldSuffix}') IS NULL"),
            "expired outbox partition should be dropped");
        Assert.True(await Db.Scalar<bool>(ds, $"SELECT to_regclass('events_delivery_{oldSuffix}') IS NULL"),
            "expired delivery partition should be dropped");
        Assert.True(await Db.Scalar<bool>(ds, $"SELECT to_regclass('events_outbox_{aheadSuffix}') IS NOT NULL"),
            "ahead partitions should be pre-created");
        Assert.Equal(0L, await Db.Scalar<long>(ds,
            "SELECT count(*) FROM events_dedupe WHERE received_at < now() - interval '30 days'"));
    }

    [Fact]
    public async Task Partitions_containing_dead_rows_survive_until_dead_retention()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        var suffix = await Db.Scalar<string>(ds, "SELECT to_char(current_date - 40, 'YYYYMMDD')");

        await Db.Exec(ds, "SELECT ep_ensure_partitions(current_date - 40)");
        await Db.Exec(ds,
            """
            INSERT INTO events_outbox (event_id, event_name, origin, occurred_at, received_at)
            VALUES (gen_random_uuid(), 'old_event', 'server',
                    current_date - 40, (current_date - 40)::timestamptz + interval '1 hour')
            """);
        await Db.Exec(ds,
            """
            INSERT INTO events_delivery (event_ref, received_at, destination, status, last_error)
            SELECT id, received_at, 'ga4', 'dead', 'gave up' FROM events_outbox
            WHERE received_at < current_date - 39
            """);

        // day is past normal retention but the dead evidence keeps it
        await PartitionMaintenance.RunOnceAsync(ds, retentionDays: 30, retentionDeadDays: 90, aheadDays: 3);
        Assert.True(await Db.Scalar<bool>(ds, $"SELECT to_regclass('events_outbox_{suffix}') IS NOT NULL"),
            "partition with dead rows must survive normal retention");

        // once past dead retention it goes regardless
        await PartitionMaintenance.RunOnceAsync(ds, retentionDays: 30, retentionDeadDays: 35, aheadDays: 3);
        Assert.True(await Db.Scalar<bool>(ds, $"SELECT to_regclass('events_outbox_{suffix}') IS NULL"),
            "partition past dead retention should be dropped");
    }
}
