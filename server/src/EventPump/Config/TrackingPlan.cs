using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EventPump.Config;

/// <summary>
/// The tracking plan (SPEC §13): the single source of truth for event names,
/// their producing origin, routing, per-destination translations (SPEC §6.2),
/// and the user-attribute allowlist (SPEC §6.1). Loaded from the
/// EP_TRACKING_PLAN file and synced into event_registry at boot.
/// </summary>
public sealed class TrackingPlan
{
    public const string AttributesSyncedEventName = "ep_attributes_synced";
    public const string MoEngageCustomerDestination = "moengage_customer";

    [JsonPropertyName("events")]
    public Dictionary<string, PlanEvent> Events { get; set; } = [];

    [JsonPropertyName("attributes")]
    public Dictionary<string, AttributeDef> Attributes { get; set; } = [];

    /// <summary>
    /// SPEC §6.2: per-destination event-name and property-key rename map.
    /// Keyed by destination code (`ga4`, `moengage`, `amplitude`, `adjust`,
    /// `meta`). Each entry lists renames for a subset of events routed to
    /// that destination. R6 forbids `name` under `adjust` — Adjust identifies
    /// events by token, not name.
    /// </summary>
    [JsonPropertyName("destinations")]
    public Dictionary<string, DestinationOverrides> Destinations { get; set; } = [];

    public static TrackingPlan Parse(string json)
    {
        var plan = System.Text.Json.JsonSerializer.Deserialize(json, PlanJsonContext.Default.TrackingPlan)
                   ?? throw new InvalidDataException("tracking plan: empty document");
        foreach (var (name, evt) in plan.Events)
        {
            if (!Model.EventName.IsValid(name))
                throw new InvalidDataException($"tracking plan: invalid event name '{name}'");
            if (evt.Origin is not ("client" or "server"))
                throw new InvalidDataException($"tracking plan: event '{name}' origin must be client|server");
            if (evt.Reserved && evt.Origin != "server")
                throw new InvalidDataException($"tracking plan: reserved event '{name}' must have origin=server");
        }
        foreach (var (name, def) in plan.Attributes)
        {
            if (!Model.AttributeName.IsValid(name))
                throw new InvalidDataException($"tracking plan: invalid attribute name '{name}'");
            if (def.Type is not ("string" or "email" or "e164" or "enum"))
                throw new InvalidDataException($"tracking plan: attribute '{name}' unknown type '{def.Type}'");
            if (def.Type == "enum" && (def.Values is null || def.Values.Length == 0))
                throw new InvalidDataException($"tracking plan: enum attribute '{name}' requires non-empty values");
        }

        // Auto-inject the reserved attribute-sync event so producers cannot use
        // the name and the SQL/HTTP registry both know about it (SPEC §6.1).
        plan.Events[AttributesSyncedEventName] = new PlanEvent
        {
            Origin = "server",
            Destinations = [MoEngageCustomerDestination],
            Reserved = true,
        };

        // SPEC §6.2 R6: Adjust identifies events by token, not name — a `name`
        // rename under `destinations.adjust` would do nothing and confuse anyone
        // who wrote it. Reject at boot.
        if (plan.Destinations.TryGetValue("adjust", out var adjustDest))
        {
            foreach (var (evtName, evt) in adjustDest.Events)
            {
                if (evt.Name is not null)
                    throw new InvalidDataException(
                        $"tracking plan: destinations.adjust.events.{evtName}.name is not allowed " +
                        "— Adjust identifies events by token, not name. Set adjust_token on the event " +
                        "and use destinations.adjust.events.<x>.properties for attribute renames.");
            }
        }

        // SPEC §6.2 R7: every event in a rename map must exist in the top-level
        // `events` block, and the destination that owns the rename must actually
        // route to that event. Otherwise the rename is unreachable dead config.
        foreach (var (destName, dest) in plan.Destinations)
        {
            foreach (var (evtName, _) in dest.Events)
            {
                if (!plan.Events.TryGetValue(evtName, out var evt))
                    throw new InvalidDataException(
                        $"tracking plan: destinations.{destName}.events references undefined event '{evtName}'");
                if (!evt.Destinations.Contains(destName))
                    throw new InvalidDataException(
                        $"tracking plan: destinations.{destName}.events.{evtName} exists but " +
                        $"'{evtName}' does not route to '{destName}'");
            }
        }

        return plan;
    }

    public static TrackingPlan Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// SPEC §6.2 R1/R2: destination-specific event name if declared,
    /// otherwise the canonical name.
    /// </summary>
    public string ResolveEventName(string canonicalName, string destination)
        => Destinations.TryGetValue(destination, out var dest)
           && dest.Events.TryGetValue(canonicalName, out var evt)
           && evt.Name is { } name
            ? name
            : canonicalName;

    /// <summary>
    /// SPEC §6.2 R3: rewrite property keys per the destination map. Keys not
    /// listed pass through as-is. Called by each event-carrying sender before
    /// it materializes its destination-specific payload. Returns the input
    /// unchanged when no rename applies.
    /// </summary>
    public string ResolvePropertiesJson(string canonicalName, string destination, string sourceJson)
    {
        if (!Destinations.TryGetValue(destination, out var dest)
            || !dest.Events.TryGetValue(canonicalName, out var evt)
            || evt.Properties is null
            || evt.Properties.Count == 0)
        {
            return sourceJson;
        }

        using var doc = JsonDocument.Parse(sourceJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return sourceJson;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var name = evt.Properties.TryGetValue(property.Name, out var renamed)
                    ? renamed
                    : property.Name;
                writer.WritePropertyName(name);
                property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}

public sealed class PlanEvent
{
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "";

    [JsonPropertyName("destinations")]
    public string[] Destinations { get; set; } = [];

    [JsonPropertyName("adjust_token")]
    public string? AdjustToken { get; set; }

    [JsonPropertyName("reserved")]
    public bool Reserved { get; set; }
}

/// <summary>SPEC §6.2: destination-specific overrides — event-name and property-key renames.</summary>
public sealed class DestinationOverrides
{
    [JsonPropertyName("events")]
    public Dictionary<string, EventOverride> Events { get; set; } = [];
}

public sealed class EventOverride
{
    /// <summary>Overrides the outbound event name (R1/R2). Rejected under `adjust` per R6.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Property key rename map: `{canonical_key: destination_key}` (R3).</summary>
    [JsonPropertyName("properties")]
    public Dictionary<string, string>? Properties { get; set; }
}

[JsonSerializable(typeof(TrackingPlan))]
internal sealed partial class PlanJsonContext : JsonSerializerContext;
