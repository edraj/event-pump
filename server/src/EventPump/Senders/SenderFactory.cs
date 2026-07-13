using EventPump.Config;
using EventPump.Worker;

namespace EventPump.Senders;

/// <summary>Builds the enabled destination senders from config (SPEC §12, §13).</summary>
public static class SenderFactory
{
    public static IReadOnlyList<IDestinationSender> Create(
        EpConfig config, TrackingPlan plan, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger("eventpump.senders");
        var senders = new List<IDestinationSender>();
        if (config.Ga4Enabled) senders.Add(new Ga4Sender(config));
        if (config.AmplitudeEnabled) senders.Add(new AmplitudeSender(config));
        if (config.MoEngageEnabled) senders.Add(new MoEngageSender(config));
        if (config.AdjustEnabled) senders.Add(new AdjustSender(config, plan));
        if (config.MetaEnabled) senders.Add(new MetaCapiSender(config, plan)); // OFF by default (SPEC §12)
        log.LogInformation("enabled destinations: {Destinations}",
            senders.Count == 0 ? "(none)" : string.Join(", ", senders.Select(s => s.Destination)));
        return senders;
    }
}
