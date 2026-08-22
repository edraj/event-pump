using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EventPump.Worker;

namespace EventPump.Senders;

internal static class SenderUtil
{
    // Utf8JsonWriter's default encoder escapes non-HTML-safe ASCII (+, =, etc.)
    // as \uXXXX. Our payloads are POSTed to destination JSON APIs, never embedded
    // in HTML, so the relaxed encoder is both safe and produces cleaner wire
    // output. Critically, this keeps E.164 phone numbers on the wire as "+964..."
    // rather than "+964...".
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Every outbound call gets an explicit timeout (SPEC ground rule).</summary>
    public static HttpClient CreateClient(int senderTimeoutMs, HttpMessageHandler? handler)
        => new(handler ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(senderTimeoutMs),
        };

    public static string WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            write(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Resolves the user id a destination should see (migration 0010).
    /// <paramref name="handle"/> is the per-destination id identify() supplied
    /// — ga4_user_id, amplitude_user_id, moengage_customer_id, meta_external_id
    /// — and it is recorded against the session row's user_id.
    ///
    /// The senders join an event to the *current* identity row, so the two can
    /// name different people: an event carrying user A's user_id may be
    /// delivered after an in-session account switch moved the row to user B.
    /// Whenever the event names a user of its own and it disagrees with the
    /// row's, the row's handle belongs to somebody else and must be ignored —
    /// shipping it would attribute A's activity to B's analytics profile.
    /// Only when the two agree (or the event names nobody) does the handle win.
    /// </summary>
    public static string? WireUserId(string? eventUserId, string? sessionUserId, string? handle)
        => eventUserId is not null && sessionUserId is not null
           && !string.Equals(eventUserId, sessionUserId, StringComparison.Ordinal)
            ? eventUserId
            : handle ?? eventUserId ?? sessionUserId;

    public static SendResult MissingIdentity(DeliveryItem item, string reason)
        => item.Identity is null ? SendResult.NoIdentity(reason) : SendResult.Skip(reason);

    /// <summary>Reads a string field from a JSON object document (null-safe).</summary>
    public static string? GetString(JsonElement root, string key)
        => root.ValueKind == JsonValueKind.Object
           && root.TryGetProperty(key, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Session start ms embedded in a UUIDv7 session_key's first 48 bits.</summary>
    public static long? SessionStartMs(Guid? sessionKey)
        => sessionKey is { } key ? Convert.ToInt64(key.ToString("N")[..12], 16) : null;

    /// <summary>
    /// Lowercase-hex SHA-256 of a UTF-8 string. Used by senders for hashed PII
    /// identifiers (GA4 user_data.sha256_email_address / sha256_phone_number,
    /// Adjust s2s_email / s2s_phone) per SPEC §6.1 mapping.
    /// </summary>
    public static string Sha256Hex(string value)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value), hash);
        return Convert.ToHexStringLower(hash);
    }
}
