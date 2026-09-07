using EventPump.Config;
using EventPump.Data;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class ErasureAuditTests(PostgresFixture pg) : IAsyncLifetime
{
    private const int Retention = 30;

    private NpgsqlDataSource _ds = null!;

    public async Task InitializeAsync() => _ds = await pg.CreateMigratedDatabaseAsync();

    public Task DisposeAsync() => _ds.DisposeAsync().AsTask();

    private Task<long> AuditCount() =>
        Db.Scalar<long>(_ds, "SELECT count(*) FROM erasure_audit");

    private Task<string> Column(string column) =>
        Db.Scalar<string>(_ds, $"SELECT {column}::text FROM erasure_audit LIMIT 1");

    [Fact]
    public async Task Records_the_request_with_everything_needed_to_prove_it()
    {
        var handles = new EventStore.ErasureHandles(
            MoEngageCustomerId: "M-1",
            AdjustDevices: [new EventStore.AdjustDevice("A-1", null, null)]);

        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", Guid.NewGuid(), handles.ToContextJson(),
            ["moengage_erasure", "adjust_erasure"], 3, default);

        Assert.Equal(1, await AuditCount());
        Assert.Equal("u-1", await Column("user_id"));
        Assert.Equal("person", await Column("variant"));
        Assert.Equal("3", await Column("cancelled_deliveries"));
        Assert.Contains("M-1", await Column("handles"));
        Assert.Contains("moengage_erasure", await Column("destinations"));
    }

    [Fact]
    public async Task Records_a_request_that_queued_nothing_at_all()
    {
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", null, "{}", [], 0, default);

        Assert.Equal(1, await AuditCount());
        Assert.Equal("{}", await Column("destinations"));
        Assert.Equal("{}", await Column("outcomes"));
    }

    [Fact]
    public async Task Starts_with_no_outcomes_recorded()
    {
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", Guid.NewGuid(), "{}", ["moengage_erasure"], 0, default);

        Assert.Equal("{}", await Column("outcomes"));
    }

    [Fact]
    public async Task Writes_a_terminal_outcome_back_onto_the_request()
    {
        var eventId = Guid.NewGuid();
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", eventId, "{}", ["moengage_erasure"], 0, default);

        await EventStore.RecordErasureOutcomeAsync(
            _ds, "zainmart", eventId, "moengage_erasure", "delivered", null, default);

        var outcomes = await Column("outcomes");
        Assert.Contains("moengage_erasure", outcomes);
        Assert.Contains("delivered", outcomes);
    }

    [Fact]
    public async Task Keeps_one_entry_per_destination()
    {
        var eventId = Guid.NewGuid();
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", eventId, "{}",
            ["moengage_erasure", "adjust_erasure"], 0, default);

        await EventStore.RecordErasureOutcomeAsync(
            _ds, "zainmart", eventId, "moengage_erasure", "delivered", null, default);
        await EventStore.RecordErasureOutcomeAsync(
            _ds, "zainmart", eventId, "adjust_erasure", "dead", "http_400", default);

        var outcomes = await Column("outcomes");
        Assert.Contains("delivered", outcomes);
        Assert.Contains("dead", outcomes);
        Assert.Contains("http_400", outcomes);
    }

    [Fact]
    public async Task A_rejection_is_recorded_as_faithfully_as_a_success()
    {
        var eventId = Guid.NewGuid();
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", eventId, "{}", ["moengage_erasure"], 0, default);

        await EventStore.RecordErasureOutcomeAsync(
            _ds, "zainmart", eventId, "moengage_erasure", "dead", "http_401", default);

        Assert.Contains("http_401", await Column("outcomes"));
    }

    [Fact]
    public async Task An_outcome_for_another_tenant_never_lands_on_this_row()
    {
        var eventId = Guid.NewGuid();
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", eventId, "{}", ["moengage_erasure"], 0, default);

        await EventStore.RecordErasureOutcomeAsync(
            _ds, "other", eventId, "moengage_erasure", "delivered", null, default);

        Assert.Equal("{}", await Column("outcomes"));
    }

    [Fact]
    public async Task Each_request_for_the_same_person_is_its_own_record()
    {
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "person", Guid.NewGuid(), "{}", ["moengage_erasure"], 0, default);
        await EventStore.RecordErasureRequestAsync(
            _ds, "zainmart", "u-1", "attributes", Guid.NewGuid(), "{}", ["moengage_erasure"], 0, default);

        Assert.Equal(2, await AuditCount());
    }

    [Fact]
    public async Task The_audit_table_is_not_partitioned_so_retention_cannot_drop_it()
    {
        var partitioned = await Db.Scalar<bool>(_ds,
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_partitioned_table pt
                JOIN pg_class c ON c.oid = pt.partrelid
                WHERE c.relname = 'erasure_audit')
            """);

        Assert.False(partitioned);
    }
}
