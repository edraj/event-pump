namespace EventPump.Worker;

/// <summary>
/// Common sender contract (SPEC §12). Implementations must never throw for
/// expected failures — they classify them into a SendResult. Unexpected
/// exceptions are treated as Retry by the worker.
/// </summary>
public interface IDestinationSender
{
    /// <summary>Tenant this sender is bound to (SPEC v1.2 §11).</summary>
    string AppId { get; }

    string Destination { get; }

    Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct);
}

public enum SendOutcome
{
    /// <summary>Accepted by the destination.</summary>
    Delivered,

    /// <summary>Transient failure; retry with backoff (SPEC §11).</summary>
    Retry,

    /// <summary>Required identity/config absent — terminal, never fabricate (SPEC §12).</summary>
    Skip,

    /// <summary>Permanent rejection; no retry will ever succeed.</summary>
    Dead,

    /// <summary>
    /// The identity row this event needs is not there yet. Unlike
    /// <see cref="Skip"/> this is NOT assumed permanent: /v1/identity and
    /// /v1/events are separate requests, so an event can outrun its identity
    /// (and both SDKs open their queue even when the identity POST failed).
    /// The worker retries while the event is younger than
    /// EP_IDENTITY_GRACE_S, then settles it as `skipped`. It never touches
    /// the circuit breaker — a missing identity says nothing about whether
    /// the destination is healthy.
    /// </summary>
    NoIdentity,
}

public readonly record struct SendResult(SendOutcome Outcome, string? Detail = null)
{
    public static SendResult Delivered() => new(SendOutcome.Delivered);
    public static SendResult Retry(string detail) => new(SendOutcome.Retry, detail);
    public static SendResult Skip(string reason) => new(SendOutcome.Skip, reason);
    public static SendResult NoIdentity(string reason) => new(SendOutcome.NoIdentity, reason);
    public static SendResult Dead(string detail) => new(SendOutcome.Dead, detail);
}

/// <summary>A claimed delivery with everything a sender needs (outbox row + identity join).</summary>
public sealed record DeliveryItem(
    string AppId,
    long EventRef,
    DateTime ReceivedAt,
    string Destination,
    int Attempts,
    Guid EventId,
    string EventName,
    string Origin,
    DateTime OccurredAt,
    string? UserId,
    Guid? AnonymousId,
    Guid? SessionKey,
    string PropertiesJson,
    string ContextJson,
    IdentitySnapshot? Identity);

/// <summary>identity_registry row joined via session_key at claim time (SPEC §12).</summary>
public sealed record IdentitySnapshot(
    Guid AnonymousId,
    string? UserId,
    int? SessionNumber,
    string? Ga4ClientId,
    string? Ga4SessionId,
    string? FirebaseAppInstanceId,
    string? AmplitudeDeviceId,
    string? AdjustAdid,
    string? AdjustPlatformAdId,
    string? Fbp,
    string? Fbc,
    string ClickIdsJson,
    string ContextJson,
    string? ClientIp,
    // Per-destination user identifiers (migration 0010). Senders pick their
    // own when set, fall back to the generic UserId when null.
    string? MoEngageCustomerId = null,
    string? Ga4UserId = null,
    string? AmplitudeUserId = null,
    string? MetaExternalId = null);
