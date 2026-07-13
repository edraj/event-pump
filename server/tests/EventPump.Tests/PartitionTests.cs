using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class PartitionTests(PostgresFixture pg)
{
    [Fact]
    public async Task Ensure_partitions_is_idempotent_and_creates_both_tables()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        var suffix = await Db.Scalar<string>(ds, "SELECT to_char(current_date + 10, 'YYYYMMDD')");

        await Db.Exec(ds, "SELECT ep_ensure_partitions(current_date + 10)");
        await Db.Exec(ds, "SELECT ep_ensure_partitions(current_date + 10)"); // idempotent

        Assert.True(await Db.Scalar<bool>(ds,
            $"SELECT to_regclass('events_outbox_{suffix}') IS NOT NULL"));
        Assert.True(await Db.Scalar<bool>(ds,
            $"SELECT to_regclass('events_delivery_{suffix}') IS NOT NULL"));
    }

    [Fact]
    public async Task Rows_land_in_their_day_partition_and_drop_removes_only_that_day()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "order_placed", "server", "ga4");
        await Db.Emit(ds, "order_placed"); // today's partition

        var suffix = await Db.Scalar<string>(ds, "SELECT to_char(current_date + 10, 'YYYYMMDD')");
        await Db.Exec(ds, "SELECT ep_ensure_partitions(current_date + 10)");
        await Db.Exec(ds,
            """
            INSERT INTO events_outbox
                (event_id, event_name, origin, occurred_at, received_at)
            VALUES
                (gen_random_uuid(), 'order_placed', 'server',
                 current_date + 10, (current_date + 10)::timestamptz + interval '1 hour')
            """);

        Assert.Equal(1L, await Db.Scalar<long>(ds, $"SELECT count(*) FROM events_outbox_{suffix}"));

        await Db.Exec(ds, $"DROP TABLE events_outbox_{suffix}, events_delivery_{suffix}");

        // future day's row gone with its partition; today's row untouched
        Assert.Equal(1L, await Db.Scalar<long>(ds, "SELECT count(*) FROM events_outbox"));
    }
}
