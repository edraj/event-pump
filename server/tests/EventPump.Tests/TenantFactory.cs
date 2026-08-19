using EventPump.Config;

namespace EventPump.Tests;

/// <summary>
/// Shared helpers for spinning a TenantConfig from the pre-v1.2 EpConfig +
/// TrackingPlan shape that many sender tests already build. The synthesised
/// tenant always uses app_id = "zainmart" to match the default in Db.cs.
/// </summary>
internal static class TenantFactory
{
    public const int TimeoutMs = 10_000;

    public static TenantConfig From(EpConfig config, TrackingPlan plan)
        => TenantConfig.FromLegacyEnvironment(config, plan);
}
