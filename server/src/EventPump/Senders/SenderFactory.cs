using EventPump.Config;
using EventPump.Worker;
using Npgsql;

namespace EventPump.Senders;

/// <summary>Builds the enabled destination senders from config (SPEC §12, §13).</summary>
public static class SenderFactory
{
    public static IReadOnlyList<IDestinationSender> Create(
        EpConfig config, TrackingPlan plan, NpgsqlDataSource dataSource, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("eventpump.senders");
        var senders = new List<IDestinationSender>();
        if (config.Ga4Enabled) senders.Add(new Ga4Sender(config, dataSource));
        if (config.AmplitudeEnabled) senders.Add(new AmplitudeSender(config, dataSource));
        if (config.MoEngageEnabled) senders.Add(new MoEngageSender(config));
        // SPEC §6.1: moengage_customer runs alongside moengage when attributes are enabled.
        if (config.MoEngageEnabled && config.MoEngageAttributesEnabled)
            senders.Add(new MoEngageCustomerSender(config, dataSource));
        if (config.AdjustEnabled) senders.Add(new AdjustSender(config, plan, dataSource));
        if (config.MetaEnabled) senders.Add(new MetaCapiSender(config, plan)); // OFF by default (SPEC §12)
        log.LogInformation("enabled destinations: {Destinations}",
            senders.Count == 0 ? "(none)" : string.Join(", ", senders.Select(s => s.Destination)));
        return senders;
    }
}
