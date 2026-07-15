using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EventPump.Model;

public sealed record RejectedEvent(int Index, string? EventId, string Reason);

public sealed record EventsResponse(int Accepted, IReadOnlyList<RejectedEvent> Rejected);

public sealed record ErrorResponse(string Error, string? Detail = null);

public sealed record HealthResponse(string Status);

public static partial class EventName
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string name) => Pattern().IsMatch(name);
}

/// <summary>User-attribute name (SPEC §6.1): same shape as event names.</summary>
public static partial class AttributeName
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,63}$")]
    private static partial Regex Pattern();

    public static bool IsValid(string name) => Pattern().IsMatch(name);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EventsResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(HealthResponse))]
public sealed partial class ApiJsonContext : JsonSerializerContext;
