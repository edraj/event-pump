using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class ClaimTests(PostgresFixture pg)
{
    // The worker's claim protocol (SPEC §11): SKIP LOCKED select + lease bump.
    private const string ClaimSelectSql =
        """
        SELECT event_ref FROM events_delivery
        WHERE destination = 'ga4'
          AND status IN ('pending', 'failed')
          AND next_attempt_at <= now()
        ORDER BY next_attempt_at, event_ref
        LIMIT 5
        FOR UPDATE SKIP LOCKED
        """;

    private const string ClaimWithLeaseSql =
        """
        WITH claimed AS (
            SELECT received_at, event_ref, destination FROM events_delivery
            WHERE destination = 'ga4'
              AND status IN ('pending', 'failed')
              AND next_attempt_at <= now()
            ORDER BY next_attempt_at, event_ref
            LIMIT 100
            FOR UPDATE SKIP LOCKED)
        UPDATE events_delivery d
        SET next_attempt_at = now() + interval '5 minutes'
        FROM claimed c
        WHERE d.received_at = c.received_at
          AND d.event_ref = c.event_ref
          AND d.destination = c.destination
        RETURNING d.event_ref
        """;

    [Fact]
    public async Task Two_concurrent_claimers_get_disjoint_rows()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "order_placed", "server", "ga4");
        for (var i = 0; i < 10; i++) await Db.Emit(ds, "order_placed");

        await using var conn1 = await ds.OpenConnectionAsync();
        await using var conn2 = await ds.OpenConnectionAsync();
        await using var tx1 = await conn1.BeginTransactionAsync();
        await using var tx2 = await conn2.BeginTransactionAsync();

        var claimed1 = await ReadRefs(conn1, tx1, ClaimSelectSql);
        var claimed2 = await ReadRefs(conn2, tx2, ClaimSelectSql);

        Assert.Equal(5, claimed1.Count);
        Assert.Equal(5, claimed2.Count);
        Assert.Empty(claimed1.Intersect(claimed2));
        Assert.Equal(10, claimed1.Union(claimed2).Count());

        await tx1.CommitAsync();
        await tx2.CommitAsync();
    }

    [Fact]
    public async Task Lease_bump_prevents_immediate_reclaim()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "order_placed", "server", "ga4");
        for (var i = 0; i < 3; i++) await Db.Emit(ds, "order_placed");

        await using var conn = await ds.OpenConnectionAsync();

        var first = await ReadRefs(conn, null, ClaimWithLeaseSql);
        var second = await ReadRefs(conn, null, ClaimWithLeaseSql);

        Assert.Equal(3, first.Count);
        Assert.Empty(second); // all leased into the future; a crashed worker's rows self-release
    }

    private static async Task<HashSet<long>> ReadRefs(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync();
        var refs = new HashSet<long>();
        while (await reader.ReadAsync()) refs.Add(reader.GetInt64(0));
        return refs;
    }
}
