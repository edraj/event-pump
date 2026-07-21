using System.Buffers;
using System.Text;
using System.Text.Json;
using EventPump.Senders;
using Npgsql;

namespace EventPump.Api;

/// <summary>
/// POST /v1/errors — thin first-party error/crash intake. Not events: reports
/// bypass the SDK queue (they must work when the SDK itself is broken), have
/// their own rate bucket, and dedupe by (day, app, stack_hash), not event_id.
/// Parsing is deliberately lenient — a malformed error report should still
/// count rather than bounce.
/// </summary>
public static class ErrorReports
{
    private const int MaxKind = 128;
    private const int MaxMessage = 1024;
    private const int MaxStack = 8192;

    public static async Task HandleAsync(HttpContext context, NpgsqlDataSource dataSource, string appId)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(context.Request.Body, default, context.RequestAborted);
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var kind = Truncate(GetString(root, "kind"), MaxKind) ?? "unknown";
            var message = Truncate(GetString(root, "message"), MaxMessage) ?? "";
            var stack = Truncate(GetString(root, "stack"), MaxStack) ?? "";

            // group by the stack when present (stable across dynamic message text)
            var stackHash = PixelPlatformSender.Sha256Lower(
                kind + "\n" + (stack.Length > 0 ? stack : message));

            var sample = BuildSample(root, kind, message, stack);

            await using var cmd = dataSource.CreateCommand(
                """
                INSERT INTO error_reports (day, app_id, stack_hash, kind, message, stack, sample)
                VALUES (current_date, $1, $2, $3, $4, $5, $6)
                ON CONFLICT (day, app_id, stack_hash) DO UPDATE SET
                    occurrences = error_reports.occurrences + 1,
                    last_seen   = now()
                """);
            cmd.Parameters.Add(new() { Value = appId });
            cmd.Parameters.Add(new() { Value = stackHash });
            cmd.Parameters.Add(new() { Value = kind });
            cmd.Parameters.Add(new() { Value = message });
            cmd.Parameters.Add(new() { Value = stack });
            cmd.Parameters.Add(new() { Value = sample, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb });
            await cmd.ExecuteNonQueryAsync(context.RequestAborted);

            context.Response.StatusCode = StatusCodes.Status204NoContent;
        }
    }

    private static string BuildSample(JsonElement root, string kind, string message, string stack)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kind);
            writer.WriteString("message", message);
            writer.WriteString("stack", stack);
            foreach (var key in (string[])
                     ["anonymous_id", "session_key", "app_version", "url", "screen"])
            {
                if (Truncate(GetString(root, key), MaxMessage) is { } value)
                    writer.WriteString(key, value);
            }
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("sdk");
                sdk.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? GetString(JsonElement root, string key)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(key, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Truncate(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}
