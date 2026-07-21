using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using EventPump.Config;

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
    public static HttpClient CreateClient(EpConfig config, HttpMessageHandler? handler)
        => new(handler ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(config.SenderTimeoutMs),
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
