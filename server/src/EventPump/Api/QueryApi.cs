using System.Buffers;
using System.Globalization;
using System.Text;
using EventPump.Config;
using Npgsql;

namespace EventPump.Api;

/// <summary>
/// Read-only query endpoints for the events UI (internal listener only; public
/// exposure is nginx's job). The time window is clamped server-side to
/// EP_QUERY_MAX_DAYS so every query prunes to a handful of daily partitions.
/// </summary>
public static class QueryApi
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static async Task EventsAsync(HttpContext context, NpgsqlDataSource dataSource, EpConfig config)
    {
        var query = context.Request.Query;
        var now = DateTimeOffset.UtcNow;
        var minFrom = now.AddDays(-config.QueryMaxDays);

        var from = ParseTime(query["from"]) ?? minFrom;
        if (from < minFrom) from = minFrom; // hard ceiling (SPEC: last few days only)
        var to = ParseTime(query["to"]) ?? now.AddMinutes(1);

        var limit = int.TryParse(query["limit"], out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, MaxLimit)
            : DefaultLimit;

        var sql = new StringBuilder(
            """
            SELECT o.id, o.received_at, o.event_id, o.event_name, o.origin, o.occurred_at,
                   o.user_id, o.anonymous_id, o.session_key,
                   o.properties::text, o.context::text,
                   coalesce(json_agg(json_build_object(
                        'destination', d.destination, 'status', d.status,
                        'attempts', d.attempts, 'last_error', d.last_error,
                        'delivered_at', d.delivered_at) ORDER BY d.destination)
                     FILTER (WHERE d.destination IS NOT NULL), '[]')::text AS deliveries
            FROM events_outbox o
            LEFT JOIN events_delivery d
                   ON d.received_at = o.received_at AND d.event_ref = o.id
            WHERE o.received_at >= @from AND o.received_at < @to
            """);

        var parameters = new List<NpgsqlParameter>
        {
            new("from", from.UtcDateTime),
            new("to", to.UtcDateTime),
        };

        void Equality(string column, string param, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sql.Append($" AND {column} = @{param}");
            parameters.Add(column.EndsWith("_id") || column.EndsWith("session_key")
                ? new NpgsqlParameter(param, Guid.TryParse(value, out var guid) ? guid : Guid.Empty)
                : new NpgsqlParameter(param, value));
        }

        Equality("o.event_name", "name", query["event_name"]);
        Equality("o.origin", "origin", query["origin"]);
        if (!string.IsNullOrEmpty(query["user_id"]))
        {
            sql.Append(" AND o.user_id = @uid");
            parameters.Add(new("uid", (string)query["user_id"]!));
        }
        Equality("o.anonymous_id", "anon", query["anonymous_id"]);
        Equality("o.session_key", "skey", query["session_key"]);

        string? status = query["status"];
        string? destination = query["destination"];
        if (!string.IsNullOrEmpty(status) || !string.IsNullOrEmpty(destination))
        {
            sql.Append(
                " AND EXISTS (SELECT 1 FROM events_delivery dx WHERE dx.received_at = o.received_at" +
                " AND dx.event_ref = o.id");
            if (!string.IsNullOrEmpty(status))
            {
                sql.Append(" AND dx.status = @st");
                parameters.Add(new("st", status));
            }
            if (!string.IsNullOrEmpty(destination))
            {
                sql.Append(" AND dx.destination = @dest");
                parameters.Add(new("dest", destination));
            }
            sql.Append(')');
        }

        if (ParseCursor(query["cursor"]) is var (cursorTime, cursorId) && cursorId != 0)
        {
            sql.Append(" AND (o.received_at, o.id) < (@cts, @cid)");
            parameters.Add(new("cts", cursorTime));
            parameters.Add(new("cid", cursorId));
        }

        sql.Append(
            """
             GROUP BY o.id, o.received_at, o.event_id, o.event_name, o.origin, o.occurred_at,
                      o.user_id, o.anonymous_id, o.session_key, o.properties, o.context
             ORDER BY o.received_at DESC, o.id DESC
             LIMIT @lim
            """);
        parameters.Add(new("lim", limit));

        await using var cmd = dataSource.CreateCommand(sql.ToString());
        foreach (var parameter in parameters) cmd.Parameters.Add(parameter);

        var buffer = new ArrayBufferWriter<byte>();
        long lastId = 0;
        DateTime lastTime = default;
        var count = 0;
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("events");
            await using (var reader = await cmd.ExecuteReaderAsync(context.RequestAborted))
            {
                while (await reader.ReadAsync(context.RequestAborted))
                {
                    count++;
                    lastId = reader.GetInt64(0);
                    lastTime = reader.GetDateTime(1);
                    writer.WriteStartObject();
                    writer.WriteString("received_at", new DateTimeOffset(lastTime, TimeSpan.Zero));
                    writer.WriteString("event_id", reader.GetGuid(2));
                    writer.WriteString("event_name", reader.GetString(3));
                    writer.WriteString("origin", reader.GetString(4));
                    writer.WriteString("occurred_at",
                        new DateTimeOffset(reader.GetDateTime(5), TimeSpan.Zero));
                    if (!reader.IsDBNull(6)) writer.WriteString("user_id", reader.GetString(6));
                    if (!reader.IsDBNull(7)) writer.WriteString("anonymous_id", reader.GetGuid(7));
                    if (!reader.IsDBNull(8)) writer.WriteString("session_key", reader.GetGuid(8));
                    writer.WritePropertyName("properties");
                    writer.WriteRawValue(reader.GetString(9));
                    writer.WritePropertyName("context");
                    writer.WriteRawValue(reader.GetString(10));
                    writer.WritePropertyName("deliveries");
                    writer.WriteRawValue(reader.GetString(11));
                    writer.WriteEndObject();
                }
            }
            writer.WriteEndArray();
            if (count == limit)
            {
                writer.WriteString("next_cursor",
                    $"{lastTime.Ticks.ToString(CultureInfo.InvariantCulture)}-{lastId}");
            }
            writer.WriteEndObject();
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
    }

    public static async Task IdentityAsync(HttpContext context, NpgsqlDataSource dataSource)
    {
        if (!Guid.TryParse(context.Request.RouteValues["sessionKey"]?.ToString(), out var sessionKey))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await using var cmd = dataSource.CreateCommand(
            """
            SELECT session_key, anonymous_id, user_id, session_number,
                   ga4_client_id, ga4_session_id, firebase_app_instance_id,
                   amplitude_device_id, adjust_adid, adjust_platform_ad_id,
                   fbp, fbc, click_ids::text, context::text, client_ip,
                   created_at, updated_at
            FROM identity_registry WHERE session_key = $1
            """);
        cmd.Parameters.Add(new() { Value = sessionKey });

        await using var reader = await cmd.ExecuteReaderAsync(context.RequestAborted);
        if (!await reader.ReadAsync(context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("session_key", reader.GetGuid(0));
            writer.WriteString("anonymous_id", reader.GetGuid(1));
            if (!reader.IsDBNull(2)) writer.WriteString("user_id", reader.GetString(2));
            if (!reader.IsDBNull(3)) writer.WriteNumber("session_number", reader.GetInt32(3));
            string[] handleColumns =
            [
                "ga4_client_id", "ga4_session_id", "firebase_app_instance_id",
                "amplitude_device_id", "adjust_adid", "adjust_platform_ad_id", "fbp", "fbc",
            ];
            for (var i = 0; i < handleColumns.Length; i++)
            {
                if (!reader.IsDBNull(4 + i)) writer.WriteString(handleColumns[i], reader.GetString(4 + i));
            }
            writer.WritePropertyName("click_ids");
            writer.WriteRawValue(reader.GetString(12));
            writer.WritePropertyName("context");
            writer.WriteRawValue(reader.GetString(13));
            if (!reader.IsDBNull(14)) writer.WriteString("client_ip", reader.GetString(14));
            writer.WriteString("created_at", new DateTimeOffset(reader.GetDateTime(15), TimeSpan.Zero));
            writer.WriteString("updated_at", new DateTimeOffset(reader.GetDateTime(16), TimeSpan.Zero));
            writer.WriteEndObject();
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted);
    }

    private static DateTimeOffset? ParseTime(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static (DateTime Time, long Id) ParseCursor(string? cursor)
    {
        if (cursor is null) return (default, 0);
        var dash = cursor.LastIndexOf('-');
        if (dash <= 0
            || !long.TryParse(cursor[..dash], out var ticks)
            || !long.TryParse(cursor[(dash + 1)..], out var id))
            return (default, 0);
        return (new DateTime(ticks, DateTimeKind.Utc), id);
    }
}
