using EventPump.Model;
using EventPump.Observability;
using Npgsql;

namespace EventPump.Worker;

/// <summary>The worker's own /metrics + /healthz listener (SPEC §9.4, §13).</summary>
public static class MetricsHost
{
    public static async Task<WebApplication> StartAsync(
        string listen, MetricsRegistry metrics, NpgsqlDataSource dataSource)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(Api.ApiApp.ParseBind(listen)));

        var app = builder.Build();

        app.MapGet("/metrics", (RequestDelegate)(context =>
        {
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            return context.Response.WriteAsync(metrics.Render());
        }));

        app.MapGet("/healthz", (RequestDelegate)(async context =>
        {
            try
            {
                await using var cmd = dataSource.CreateCommand("SELECT 1");
                await cmd.ExecuteScalarAsync(context.RequestAborted);
                await context.Response.WriteAsJsonAsync(
                    new HealthResponse("ok"), ApiJsonContext.Default.HealthResponse);
            }
            catch (Exception)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(
                    new HealthResponse("db_unreachable"), ApiJsonContext.Default.HealthResponse);
            }
        }));

        await app.StartAsync();
        return app;
    }
}
