using Npgsql;
using Xunit;

namespace EventPump.Tests;

[Collection("pg")]
public class ProducerGrantTests(PostgresFixture pg)
{
    private const string FunctionSignature =
        "emit_event(text, jsonb, text, uuid, uuid, jsonb, timestamptz, uuid)";

    [Fact]
    public async Task Emit_event_is_the_sole_doorway_for_producer_roles()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        await Db.RegisterEvent(ds, "order_placed", "server", "ga4");

        var role = $"ep_prod_{Guid.NewGuid():N}"[..24];
        await using var conn = await ds.OpenConnectionAsync();

        try
        {
            await Exec(conn, $"CREATE ROLE {role} NOLOGIN");
        }
        catch (PostgresException ex) when (ex.SqlState == "42501")
        {
            // environment without CREATEROLE (e.g. restricted local PG role);
            // CI runs as superuser and always exercises this test fully
            Console.WriteLine("SKIPPED Emit_event_is_the_sole_doorway: test role lacks CREATEROLE");
            return;
        }

        try
        {
            await Exec(conn, $"GRANT {role} TO CURRENT_USER");
            // the documented production grant: EXECUTE on the function, nothing else
            await Exec(conn, $"GRANT EXECUTE ON FUNCTION {FunctionSignature} TO {role}");

            await Exec(conn, $"SET ROLE {role}");

            // the doorway works: SECURITY DEFINER writes the outbox on the role's behalf
            var anon = Guid.NewGuid();
            await using (var emit = new NpgsqlCommand(
                "SELECT emit_event('order_placed', p_anonymous_id => $1)", conn))
            {
                emit.Parameters.Add(new() { Value = anon });
                Assert.NotNull(await emit.ExecuteScalarAsync());
            }

            // direct table access is denied — insert, select, and dedupe bypass
            foreach (var attempt in (string[])
            [
                "INSERT INTO events_outbox (event_id, event_name, origin, occurred_at) " +
                "VALUES (gen_random_uuid(), 'order_placed', 'server', now())",
                "SELECT count(*) FROM events_outbox",
                "INSERT INTO events_dedupe (event_id) VALUES (gen_random_uuid())",
                "SELECT count(*) FROM identity_registry",
            ])
            {
                var denied = await Assert.ThrowsAsync<PostgresException>(() => Exec(conn, attempt));
                Assert.Equal("42501", denied.SqlState); // insufficient_privilege
            }

            await Exec(conn, "RESET ROLE");

            // the emit really landed, fanned out, and ran the first-visit gate
            Assert.Equal(1L, await Db.Scalar<long>(ds,
                $"SELECT count(*) FROM events_outbox WHERE anonymous_id = '{anon}' AND event_name = 'order_placed'"));
            Assert.Equal(1L, await Db.Scalar<long>(ds,
                $"SELECT count(*) FROM first_seen WHERE anonymous_id = '{anon}'"));
        }
        finally
        {
            await Exec(conn, "RESET ROLE");
            await Exec(conn, $"DROP OWNED BY {role}");
            await Exec(conn, $"DROP ROLE {role}");
        }
    }

    [Fact]
    public async Task Emit_event_execute_is_revoked_from_public()
    {
        var ds = await pg.CreateMigratedDatabaseAsync();
        Assert.False(await Db.Scalar<bool>(ds,
            $"SELECT has_function_privilege('pg_signal_backend', '{FunctionSignature}', 'EXECUTE')"),
            "PUBLIC must not retain the default EXECUTE grant on emit_event");
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
