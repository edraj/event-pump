using System.Text.Json;
using EventPump.Worker;

namespace EventPump.Senders;

internal static class ErasureHttp
{
    // 4xx other than throttling means the destination rejected this erasure
    // and will keep rejecting it. Retrying forever would hide a real failure
    // to delete someone's data behind a `pending` row nobody reads.
    //
    // This deliberately does NOT share the event path's SenderUtil.MapStatus
    // treatment of 401/403 as AuthFailed, even though the cause is the same
    // wrong key. On a legally clocked deletion, retrying a credential failure
    // makes it *less* recoverable, not more:
    //
    //   1. DeliveryWorker only writes the erasure audit outcome once the row
    //      settles, so a retrying 401 leaves the DSR audit trail reading
    //      `pending` for the whole backoff ladder — exactly the unread
    //      `pending` row this comment warns about.
    //   2. EnqueueErasureAsync treats a `failed` row as already covered, so
    //      the operator who fixes the key and re-issues the request is told
    //      `Queued: []` and nothing happens. Dead on the first attempt keeps
    //      the re-drive working — see the "guard is per destination" comment
    //      on EnqueueErasureAsync.
    //
    // So the erasure answer to a bad key is: settle now, name it in the audit
    // trail, and let the operator re-drive once the credential is fixed.
    public static SendResult Map(int status) =>
        status is 429 || status >= 500
            ? SendResult.Retry($"http_{status}")
            // Named distinctly so the audit trail says *why* the erasure was
            // refused. An operator reading `http_400` looks at the payload; one
            // reading `http_401_bad_credentials` looks at the tenant file.
            : status is 401 or 403
                ? SendResult.Dead($"http_{status}_bad_credentials")
                : SendResult.Dead($"http_{status}");

    // The reserved erasure event carries no session_key, so identity_registry
    // is out of reach here; the enqueue stamped the handles on the context.
    public static string Handle(string contextJson, string key, string fallback)
    {
        using var ctx = JsonDocument.Parse(contextJson);
        return SenderUtil.GetString(ctx.RootElement, key) ?? fallback;
    }
}
