using EventPump.Config;
using EventPump.Worker;
using Npgsql;

namespace EventPump.Senders;

/// <summary>
/// Builds the enabled destination senders for every tenant (SPEC v1.2 §11, §12).
/// The worker runs one pipeline per (app_id, destination) pair, so this list
/// materialises those pairs at boot: same tenant × destination is one HTTP
/// client, one breaker, one claim loop.
/// </summary>
public static class SenderFactory
{
    public static IReadOnlyList<IDestinationSender> Create(
        EpConfig config, TenantRegistry tenants, NpgsqlDataSource dataSource, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("eventpump.senders");
        var senders = new List<IDestinationSender>();
        foreach (var tenant in tenants.All)
        {
            var timeout = config.SenderTimeoutMs;
            if (tenant.Ga4Enabled) senders.Add(new Ga4Sender(tenant, timeout, dataSource));
            if (tenant.AmplitudeEnabled) senders.Add(new AmplitudeSender(tenant, timeout, dataSource));
            if (tenant.MoEngageEnabled) senders.Add(new MoEngageSender(tenant, timeout));
            // SPEC §6.1: moengage_customer runs alongside moengage. Registered even
            // when the tenant's attributes flag is off — the worker runs one
            // pipeline per registered sender, so skipping registration would leave
            // rows enqueued before the flag flipped stuck in `pending` forever
            // instead of resolving to `skipped: attributes_disabled` (SPEC §12).
            if (tenant.MoEngageEnabled) senders.Add(new MoEngageCustomerSender(tenant, timeout, dataSource));
            if (tenant.AdjustEnabled) senders.Add(new AdjustSender(tenant, timeout, dataSource));
            if (tenant.MetaEnabled) senders.Add(new MetaCapiSender(tenant, timeout, dataSource)); // OFF by default (SPEC §12)
        }
        log.LogInformation("enabled tenant × destination pipelines: {Count} — {Pipelines}",
            senders.Count,
            senders.Count == 0 ? "(none)" : string.Join(", ", senders.Select(s => $"{s.AppId}/{s.Destination}")));
        return senders;
    }
}
