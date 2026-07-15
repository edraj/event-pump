using System.Text.Json.Serialization;

namespace EventPump.Config;

/// <summary>
/// The tracking plan (SPEC §13): the single source of truth for event names,
/// their producing origin, routing, per-destination translations, and the
/// user-attribute allowlist (SPEC §6.1). Loaded from the EP_TRACKING_PLAN
/// file and synced into event_registry at boot.
/// </summary>
public sealed class TrackingPlan
{
    public const string AttributesSyncedEventName = "ep_attributes_synced";
    public const string MoEngageCustomerDestination = "moengage_customer";

    [JsonPropertyName("events")]
    public Dictionary<string, PlanEvent> Events { get; set; } = [];

    [JsonPropertyName("attributes")]
    public Dictionary<string, AttributeDef> Attributes { get; set; } = [];

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

        return plan;
    }

    public static TrackingPlan Load(string path) => Parse(File.ReadAllText(path));
}

public sealed class PlanEvent
{
    [JsonPropertyName("origin")]
    public string Origin { get; set; } = "";

    [JsonPropertyName("destinations")]
    public string[] Destinations { get; set; } = [];

    [JsonPropertyName("meta_name")]
    public string? MetaName { get; set; }

    [JsonPropertyName("adjust_token")]
    public string? AdjustToken { get; set; }

    [JsonPropertyName("reserved")]
    public bool Reserved { get; set; }
}

[JsonSerializable(typeof(TrackingPlan))]
internal sealed partial class PlanJsonContext : JsonSerializerContext;
