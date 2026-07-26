using System.Net;
using System.Text.Json;
using EventPump.Api;
using EventPump.Config;
using EventPump.Observability;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EventPump.Tests;

/// <summary>
/// /docs serves a hand-maintained spec, so the job of these tests is to keep it
/// honest: the route list in Api/openapi.json must match the routes the app
/// actually maps. No database needed — the data source is never opened.
/// </summary>
public class OpenApiTests : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;
    private HttpClient _pub = null!;
    private HttpClient _int = null!;

    public async Task InitializeAsync()
    {
        _ds = NpgsqlDataSource.Create("Host=127.0.0.1;Username=none;Database=none");
        var plan = TrackingPlan.Parse("""{"events":{}}""");
        _api = await ApiApp.StartAsync(new EpConfig
        {
            DbConnString = "unused-in-tests",
            Listen = "http://127.0.0.1:0",
            InternalListen = "http://127.0.0.1:0",
            ClientTokens = new() { ["tok-web"] = "webapp" },
            InternalToken = "internal-secret",
        }, _ds, plan, new MetricsRegistry());
        _pub = new HttpClient { BaseAddress = _api.PublicBaseUri };
        _int = new HttpClient { BaseAddress = _api.InternalBaseUri };
    }

    public async Task DisposeAsync()
    {
        _pub.Dispose();
        _int.Dispose();
        await _api.DisposeAsync();
        await _ds.DisposeAsync();
    }

    /// <summary>
    /// The drift guard: every mapped route is documented and every documented
    /// route is mapped. Add or rename an endpoint without touching
    /// Api/openapi.json and this fails.
    /// </summary>
    [Fact]
    public void Spec_covers_exactly_the_routes_the_app_maps()
    {
        var mapped = _api.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => !endpoint.RoutePattern.RawText!.StartsWith("/docs", StringComparison.Ordinal))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods
                .Select(method => $"{method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}"))
            .Order()
            .ToArray();

        using var document = JsonDocument.Parse(OpenApiDocs.Spec);
        var documented = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Select(operation => $"{operation.Name.ToUpperInvariant()} {path.Name}"))
            .Order()
            .ToArray();

        Assert.Equal(mapped, documented);
    }

    [Fact]
    public void Spec_is_valid_json_with_the_expected_shape()
    {
        using var document = JsonDocument.Parse(OpenApiDocs.Spec);
        var root = document.RootElement;

        Assert.StartsWith("3.", root.GetProperty("openapi").GetString());
        Assert.Equal("Event Pump API", root.GetProperty("info").GetProperty("title").GetString());
        // ".." keeps "Try it out" working behind a path-prefixed reverse proxy
        Assert.Equal("..", root.GetProperty("servers")[0].GetProperty("url").GetString());
        // the Authorize button in Swagger UI
        Assert.Equal("bearer", root.GetProperty("components").GetProperty("securitySchemes")
            .GetProperty("Bearer").GetProperty("scheme").GetString());

        // request bodies are what make the page usable — spot-check the busiest one
        var events = root.GetProperty("paths").GetProperty("/v1/events").GetProperty("post");
        Assert.Contains("EventsRequest", events.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        // every $ref must resolve
        var schemas = root.GetProperty("components").GetProperty("schemas");
        foreach (var reference in References(root))
        {
            Assert.True(schemas.TryGetProperty(reference, out _), $"dangling $ref: {reference}");
        }
    }

    [Fact]
    public async Task Docs_are_served_on_both_listeners()
    {
        foreach (var client in (HttpClient[])[_pub, _int])
        {
            var page = await client.GetAsync("/docs");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("swagger-ui", await page.Content.ReadAsStringAsync());

            var spec = await client.GetAsync("/docs/openapi.json");
            Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
            Assert.Equal(OpenApiDocs.Spec, await spec.Content.ReadAsStringAsync());
        }
    }

    private static IEnumerable<string> References(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("$ref") && property.Value.GetString() is { } reference)
                    yield return reference["#/components/schemas/".Length..];
                foreach (var nested in References(property.Value)) yield return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var nested in References(item)) yield return nested;
            }
        }
    }
}
