using System.Runtime.CompilerServices;

namespace EventPump.Tests;

internal static class TestEnv
{
    // Fedora dev box runs rootless podman, not docker. Point Testcontainers at the
    // user podman socket when DOCKER_HOST is not set; ryuk needs privileged docker
    // socket mounts, so disable it for podman (containers are cleaned up on DisposeAsync).
    [ModuleInitializer]
    internal static void Init()
    {
        if (Environment.GetEnvironmentVariable("DOCKER_HOST") is not null) return;
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (runtimeDir is null) return;
        var sock = Path.Combine(runtimeDir, "podman", "podman.sock");
        if (!Path.Exists(sock)) return;
        Environment.SetEnvironmentVariable("DOCKER_HOST", $"unix://{sock}");
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }
}

internal static class RepoPaths
{
    public static string ServerRoot { get; } = Find();
    public static string MigrationsDir => Path.Combine(ServerRoot, "migrations");
    public static string ProducerContract => Path.Combine(ServerRoot, "sql", "producer_contract.sql");

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EventPump.slnx")) ||
                File.Exists(Path.Combine(dir.FullName, "EventPump.sln"))) return dir.FullName;
            dir = dir.Parent!;
        }
        throw new InvalidOperationException($"EventPump.slnx not found above {AppContext.BaseDirectory}");
    }
}
