using System.Text.Json.Serialization;

namespace EventPump.Config;

/// <summary>
/// Allowlisted user-attribute definition (SPEC §6.1). Loaded from the
/// tracking-plan JSON's `attributes` block; consulted by IdentityValidation
/// to accept, normalize, and length-cap incoming attribute values.
/// </summary>
public sealed class AttributeDef
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("max_length")]
    public int? MaxLength { get; set; }

    [JsonPropertyName("values")]
    public string[]? Values { get; set; }
}
