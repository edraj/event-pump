using System.Buffers;
using System.Text;
using System.Text.Json;

namespace EventPump.Senders;

/// <summary>
/// Phase 2 structural transform for GA4 (SPEC §12): builds the nested
/// `items[]` array GA4 e-commerce events require, from our canonical flat
/// property shape. Called by Ga4Sender when the canonical event name is in
/// one of the known e-commerce sets. Purely a shape transform — the plan's
/// property-rename map still governs the scalar top-level params
/// (`currency`, `value`, `transaction_id`, `coupon`, etc.).
///
/// Item-field mapping is fixed (not plan-configurable) — matches Google's
/// documented GA4 e-commerce schema and the web-svelte dataLayer shape
/// referenced by docs/ga4-events.md:
///     product_id     -> item_id
///     product_name   -> item_name
///     brand_name     -> item_brand
///     category_name  -> item_category
///     variant_name   -> item_variant
///     price          -> price
///     quantity       -> quantity   (defaults to 1 when absent)
/// </summary>
public static class Ga4EcommerceTransform
{
    // Events that carry a single product's fields flat (view_item, add_to_cart, etc.).
    private static readonly HashSet<string> SingleProduct =
    [
        "product_viewed",
        "product_added",
        "product_removed",
        "product_added_to_wishlist",
        "product_removed_from_wishlist",
    ];

    // Events that carry a nested `products: [{...}]` array (view_cart, begin_checkout, purchase).
    private static readonly HashSet<string> MultiProduct =
    [
        "cart_viewed",
        "checkout_started",
        "order_completed",
    ];

    /// <summary>Does this canonical event require an items[] transform for GA4?</summary>
    public static bool NeedsItems(string canonicalEventName)
        => SingleProduct.Contains(canonicalEventName) || MultiProduct.Contains(canonicalEventName);

    /// <summary>
    /// Builds the items[] array as raw JSON text. Empty when the source has
    /// no product identity (e.g. an order_completed that doesn't carry a
    /// products list yet). Callers should skip emitting `items` when the
    /// returned string is `[]`.
    /// </summary>
    public static string BuildItemsJson(string canonicalEventName, JsonElement canonicalProps)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            if (canonicalProps.ValueKind == JsonValueKind.Object)
            {
                if (SingleProduct.Contains(canonicalEventName))
                {
                    if (HasProductIdentity(canonicalProps))
                        WriteItem(writer, canonicalProps);
                }
                else if (MultiProduct.Contains(canonicalEventName)
                         && canonicalProps.TryGetProperty("products", out var products)
                         && products.ValueKind == JsonValueKind.Array)
                {
                    foreach (var product in products.EnumerateArray())
                    {
                        if (product.ValueKind != JsonValueKind.Object) continue;
                        if (!HasProductIdentity(product)) continue;
                        WriteItem(writer, product);
                    }
                }
            }
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static bool HasProductIdentity(JsonElement source)
        => source.TryGetProperty("product_id", out var pid)
           && pid.ValueKind == JsonValueKind.String
           && !string.IsNullOrEmpty(pid.GetString());

    private static void WriteItem(Utf8JsonWriter writer, JsonElement source)
    {
        writer.WriteStartObject();
        WriteMapped(writer, source, "product_id",    "item_id");
        WriteMapped(writer, source, "product_name",  "item_name");
        WriteMapped(writer, source, "brand_name",    "item_brand");
        WriteMapped(writer, source, "category_name", "item_category");
        WriteMapped(writer, source, "variant_name",  "item_variant");
        WriteMapped(writer, source, "price",         "price");
        // quantity defaults to 1 (matches the web-svelte MapProduct/MapItem behavior).
        if (source.TryGetProperty("quantity", out var q) && q.ValueKind == JsonValueKind.Number)
        {
            writer.WritePropertyName("quantity");
            q.WriteTo(writer);
        }
        else
        {
            writer.WriteNumber("quantity", 1);
        }
        writer.WriteEndObject();
    }

    private static void WriteMapped(Utf8JsonWriter writer, JsonElement source, string src, string dst)
    {
        if (source.TryGetProperty(src, out var value) && value.ValueKind != JsonValueKind.Null)
        {
            writer.WritePropertyName(dst);
            value.WriteTo(writer);
        }
    }
}
