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
        // Also: any property key declared under a destination must be in the
        // event's base `properties` schema (when one is declared).
        foreach (var (destName, dest) in plan.Destinations)
        {
            foreach (var (evtName, overrides) in dest.Events)
            {
                if (!plan.Events.TryGetValue(evtName, out var evt))
                    throw new InvalidDataException(
                        $"tracking plan: destinations.{destName}.events references undefined event '{evtName}'");
                if (!evt.Destinations.Contains(destName))
                    throw new InvalidDataException(
                        $"tracking plan: destinations.{destName}.events.{evtName} exists but " +
                        $"'{evtName}' does not route to '{destName}'");
                if (evt.Properties is { } baseProps && overrides.Properties is { } destProps)
                {
                    foreach (var (canonical, _) in destProps)
                    {
                        if (!baseProps.Contains(canonical))
                            throw new InvalidDataException(
                                $"tracking plan: destinations.{destName}.events.{evtName}.properties " +
                                $"references '{canonical}' which is not in the base properties of '{evtName}'");
                    }
                }
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
    /// SPEC §6.2: filter + rename incoming properties for a destination.
    /// When `destinations.&lt;dest&gt;.events.&lt;x&gt;.properties` is declared,
    /// only listed canonical keys are forwarded (renamed to their destination
    /// key) — unlisted keys are DROPPED. When no map is declared, all base
    /// properties pass through as-is (R1 fallback). Called by each
    /// event-carrying sender before it materializes its payload.
    /// </summary>
    public string ResolvePropertiesJson(string canonicalName, string destination, string sourceJson)
    {
        if (!Destinations.TryGetValue(destination, out var dest)
            || !dest.Events.TryGetValue(canonicalName, out var evt)
            || evt.Properties is null)
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
                if (!evt.Properties.TryGetValue(property.Name, out var renamed)) continue;
                writer.WritePropertyName(renamed);
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

    /// <summary>
    /// Declared property schema for this event (SPEC §6.2). Documentation
    /// for what the app SHOULD send; ingest does NOT reject extras. Used at
    /// plan-load to verify per-destination property maps are subsets.
    /// </summary>
    [JsonPropertyName("properties")]
    public string[]? Properties { get; set; }
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
