using System.Runtime.InteropServices;
using EventPump;
using EventPump.Api;
using EventPump.Config;
using EventPump.Data;
using EventPump.Observability;
using EventPump.Senders;
using EventPump.Worker;
using Npgsql;

return args.FirstOrDefault() switch
{
    "migrate" => await RunMigrate(),
    "api" => await RunApi(),
    "worker" => await RunWorker(),
    "standalone" => await RunStandalone(),
    _ => Usage(),
};

static async Task<int> RunStandalone()
{
    var config = EpConfig.FromEnvironment();
    var plan = TrackingPlan.Load(config.TrackingPlanPath);
    await using var dataSource = NpgsqlDataSource.Create(config.DbConnString);
    await RegistrySync.SyncAsync(dataSource, plan);

    var metrics = new MetricsRegistry();
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddJsonConsole());
    var senders = SenderFactory.Create(config, plan, dataSource, loggerFactory);
    var host = await StandaloneHost.StartAsync(config, dataSource, plan, senders, metrics, loggerFactory);

    using var cts = new CancellationTokenSource();
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
    {
        signal.Cancel = true;
        cts.Cancel();
    });
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal =>
    {
        signal.Cancel = true;
        cts.Cancel();
    });

    Console.WriteLine(
        $"eventpump standalone: api on {host.Api.PublicBaseUri} (internal {host.Api.InternalBaseUri}); worker in-process");
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    await host.StopAsync();
    return 0;
}

static async Task<int> RunWorker()
{
    var config = EpConfig.FromEnvironment();
    var plan = TrackingPlan.Load(config.TrackingPlanPath);
    await using var dataSource = NpgsqlDataSource.Create(config.DbConnString);
    await RegistrySync.SyncAsync(dataSource, plan);

    var metrics = new MetricsRegistry();
    using var loggerFactory = LoggerFactory.Create(logging => logging.AddJsonConsole());
    var senders = SenderFactory.Create(config, plan, dataSource, loggerFactory);
    var worker = new DeliveryWorker(config, dataSource, senders, metrics, loggerFactory);

    // Graceful SIGTERM (SPEC §11): stop claiming, drain in-flight, release claims.
    using var cts = new CancellationTokenSource();
    using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
    {
        signal.Cancel = true;
        cts.Cancel();
    });
    using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, signal =>
    {
        signal.Cancel = true;
        cts.Cancel();
    });

    var metricsApp = await MetricsHost.StartAsync(config.MetricsListen, metrics, dataSource);
    Console.WriteLine($"eventpump worker running; metrics on {config.MetricsListen}");
    await worker.RunAsync(cts.Token);
    await metricsApp.StopAsync();
    return 0;
}

static async Task<int> RunApi()
{
    var config = EpConfig.FromEnvironment();
    var plan = TrackingPlan.Load(config.TrackingPlanPath);
    await using var dataSource = NpgsqlDataSource.Create(config.DbConnString);
    await RegistrySync.SyncAsync(dataSource, plan);
    var running = await ApiApp.StartAsync(config, dataSource, plan, new MetricsRegistry());
    Console.WriteLine(
        $"eventpump api listening on {running.PublicBaseUri} (internal {running.InternalBaseUri})");
    await running.App.WaitForShutdownAsync();
    return 0;
}

static async Task<int> RunMigrate()
{
    var connString = Environment.GetEnvironmentVariable("EP_DB_CONNSTRING");
    if (string.IsNullOrWhiteSpace(connString))
    {
        Console.Error.WriteLine("EP_DB_CONNSTRING is required");
        return 64;
    }
    // Resolution order: env var, alongside the binary, RPM install location.
    var baseDir = AppContext.BaseDirectory;
    var migrationsDir = Environment.GetEnvironmentVariable("EP_MIGRATIONS_DIR")
                        ?? FirstExisting(Directory.Exists,
                            Path.Combine(baseDir, "migrations"),
                            "/usr/share/eventpump/migrations");
    var contractPath = Environment.GetEnvironmentVariable("EP_PRODUCER_CONTRACT")
                       ?? FirstExisting(File.Exists,
                            Path.Combine(baseDir, "sql", "producer_contract.sql"),
                            "/usr/share/eventpump/sql/producer_contract.sql");

    await using var dataSource = NpgsqlDataSource.Create(connString);
    await MigrationRunner.ApplyAsync(dataSource, migrationsDir, contractPath,
        log: line => Console.WriteLine(line));
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: eventpump <api|worker|standalone|migrate>");
    return 64;
}

static string FirstExisting(Func<string, bool> exists, params string[] candidates)
    => candidates.FirstOrDefault(exists) ?? candidates[^1];
