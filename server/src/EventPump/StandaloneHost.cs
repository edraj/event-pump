using EventPump.Api;
using EventPump.Config;
using EventPump.Observability;
using EventPump.Worker;
using Npgsql;

namespace EventPump;

/// <summary>
/// `eventpump standalone`: the ingestion API and the delivery worker in ONE
/// process, sharing a metrics registry (both halves render on the API's
/// internal /metrics). For dev boxes and small single-VM installs; production
/// keeps the two-service split (see README — asymmetric recoverability).
/// </summary>
public sealed class StandaloneHost
{
    private readonly Task _workerRun;
    private readonly CancellationTokenSource _stop;

    private StandaloneHost(RunningApi api, Task workerRun, CancellationTokenSource stop)
    {
        Api = api;
        _workerRun = workerRun;
        _stop = stop;
    }

    public RunningApi Api { get; }

    public static async Task<StandaloneHost> StartAsync(
        EpConfig config,
        NpgsqlDataSource dataSource,
        TrackingPlan plan,
        IReadOnlyList<IDestinationSender> senders,
        MetricsRegistry metrics,
        ILoggerFactory loggerFactory)
    {
        var api = await ApiApp.StartAsync(config, dataSource, plan, metrics);
        var worker = new DeliveryWorker(config, dataSource, senders, metrics, loggerFactory);
        var stop = new CancellationTokenSource();
        return new StandaloneHost(api, worker.RunAsync(stop.Token), stop);
    }

    /// <summary>Worker drains and releases claims first, then the API stops.</summary>
    public async Task StopAsync()
    {
        _stop.Cancel();
        await _workerRun;
        await Api.App.StopAsync();
        _stop.Dispose();
    }
}
