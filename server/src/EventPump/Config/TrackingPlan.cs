using System.Text.Json.Serialization;

namespace EventPump.Config;

/// <summary>
/// The tracking plan (SPEC §13): the single source of truth for event names,
/// their producing origin, routing, and per-destination translations. Loaded
/// from the EP_TRACKING_PLAN file and synced into event_registry at boot.
/// </summary>
public sealed class TrackingPlan
{
    [JsonPropertyName("events")]
    public Dictionary<string, PlanEvent> Events { get; set; } = [];

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
        }
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
}

[JsonSerializable(typeof(TrackingPlan))]
internal sealed partial class PlanJsonContext : JsonSerializerContext;
