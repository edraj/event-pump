using System.Text.Json;
using EventPump.Worker;

namespace EventPump.Senders;

internal static class ErasureHttp
{
    // 4xx other than throttling means the destination rejected this erasure
    // and will keep rejecting it. Retrying forever would hide a real failure
    // to delete someone's data behind a `pending` row nobody reads.
    public static SendResult Map(int status) =>
        status is 429 || status >= 500
            ? SendResult.Retry($"http_{status}")
            : SendResult.Dead($"http_{status}");

    // The reserved erasure event carries no session_key, so identity_registry
    // is out of reach here; the enqueue stamped the handles on the context.
    public static string Handle(string contextJson, string key, string fallback)
    {
        using var ctx = JsonDocument.Parse(contextJson);
        return SenderUtil.GetString(ctx.RootElement, key) ?? fallback;
    }

    public static string? HandleOrNull(string contextJson, string key)
    {
        using var ctx = JsonDocument.Parse(contextJson);
        return SenderUtil.GetString(ctx.RootElement, key);
    }
}
