using System.Buffers;
using System.Text;
using System.Text.Json;
using EventPump.Config;

namespace EventPump.Senders;

internal static class SenderUtil
{
    /// <summary>Every outbound call gets an explicit timeout (SPEC ground rule).</summary>
    public static HttpClient CreateClient(EpConfig config, HttpMessageHandler? handler)
        => new(handler ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromMilliseconds(config.SenderTimeoutMs),
        };

    public static string WriteJson(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
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
}
