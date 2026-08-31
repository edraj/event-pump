using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>
/// GA4 erasure is not implemented. Deletion there goes through the Google
/// Analytics User Deletion API, which is OAuth-authenticated — a credential
/// this service does not hold, unlike the api_secret the event sender uses.
///
/// This exists rather than being left out so the pipeline is registered and
/// every queued row reaches a terminal state. Omitting the sender would strand
/// rows `pending` forever, and dropping ga4_erasure from the fan-out would
/// leave no record that GA4 was not erased. Skipping records the gap on each
/// request instead, which is what the audit trail is for.
/// </summary>
public sealed class Ga4ErasureSender(TenantConfig tenant) : IDestinationSender
{
    public string AppId => tenant.AppId;
    public string Destination => TrackingPlan.Ga4ErasureDestination;

    public Task<SendResult> SendAsync(DeliveryItem item, CancellationToken ct) =>
        Task.FromResult(SendResult.Skip("ga4_oauth_not_configured"));
}
