using EventPump.Data;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class MigrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Applies_all_migrations_and_is_idempotent()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();

        string[] tables =
        [
            "events_outbox", "events_dedupe", "events_delivery",
            "identity_registry", "first_seen", "event_registry", "schema_migrations",
        ];
        foreach (var table in tables)
            Assert.True(
                await Db.Scalar<bool>(ds, $"SELECT to_regclass('{table}') IS NOT NULL"),
                $"table {table} missing");

        Assert.True(
            await Db.Scalar<bool>(ds, "SELECT to_regproc('emit_event') IS NOT NULL"),
            "emit_event function missing");

        // partitions pre-created so ingestion works immediately after migrate
        Assert.True(await Db.Scalar<bool>(ds,
            "SELECT to_regclass('events_outbox_' || to_char(current_date, 'YYYYMMDD')) IS NOT NULL"));
        Assert.True(await Db.Scalar<bool>(ds,
            "SELECT to_regclass('events_delivery_' || to_char(current_date, 'YYYYMMDD')) IS NOT NULL"));

        // re-run is a no-op
        var before = await Db.Scalar<long>(ds, "SELECT count(*) FROM schema_migrations");
        Assert.True(before > 0);
        await MigrationRunner.ApplyAsync(ds, RepoPaths.MigrationsDir, RepoPaths.ProducerContract);
        var after = await Db.Scalar<long>(ds, "SELECT count(*) FROM schema_migrations");
        Assert.Equal(before, after);

        // first_visit is pre-seeded so emit_event's internal emission can never
        // fail a producer's business transaction
        Assert.Equal(1L, await Db.Scalar<long>(ds,
            "SELECT count(*) FROM event_registry WHERE event_name = 'first_visit' AND origin = 'server'"));
    }
}
