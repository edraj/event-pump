using EventPump.Config;

namespace EventPump.Tests;

/// <summary>
/// Shared helpers for spinning a TenantConfig from the pre-v1.2 EpConfig +
/// TrackingPlan shape that many sender tests already build. The synthesised
/// tenant uses Db.DefaultAppId unless the config names its own.
/// </summary>
internal static class TenantFactory
{
    public const int TimeoutMs = 10_000;

    public static TenantConfig From(EpConfig config, TrackingPlan plan)
        => TenantConfig.FromLegacyEnvironment(
            config.LegacyAppId is "" ? config with { LegacyAppId = Db.DefaultAppId } : config, plan);
}
