using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    /// <summary>Path-item keys that are operations; the rest (summary, parameters, …) are not.</summary>
    private static readonly HashSet<string> HttpMethods =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    private NpgsqlDataSource _ds = null!;
    private RunningApi _api = null!;
    private HttpClient _pub = null!;
    private HttpClient _int = null!;

    public async Task InitializeAsync()
    {
        _ds = DocsHost.DataSource();
        _api = await DocsHost.StartAsync(_ds);
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
            .SelectMany(endpoint =>
                (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(method => $"{method.ToUpperInvariant()} {endpoint.RoutePattern.RawText}"))
            .Order()
            .ToArray();

        using var document = JsonDocument.Parse(OpenApiDocs.Spec);
        var documented = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(member => HttpMethods.Contains(member.Name))
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
        // ".." resolves against /docs/openapi.json, so "Try it out" hits the API
        // root — and keeps hitting it behind a path-prefixed reverse proxy
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

        // every tag an operation uses must be declared at the root
        var declared = root.GetProperty("tags").EnumerateArray()
            .Select(tag => tag.GetProperty("name").GetString()).ToHashSet();
        foreach (var tag in Operations(root).SelectMany(op => op.TryGetProperty("tags", out var tags)
                     ? tags.EnumerateArray().Select(t => t.GetString())
                     : []))
        {
            Assert.Contains(tag, declared);
        }
    }

    /// <summary>
    /// The spec must not imply a token gate that the handler does not enforce:
    /// these four are reachable with no credential at all, and readers who
    /// believe otherwise expose the query API. See ApiApp's listener gate.
    /// </summary>
    [Theory]
    [InlineData("/internal/v1/query/events")]
    [InlineData("/internal/v1/query/identity/{sessionKey}")]
    [InlineData("/healthz")]
    [InlineData("/metrics")]
    public void Endpoints_without_a_token_check_are_documented_as_unauthenticated(string route)
    {
        using var document = JsonDocument.Parse(OpenApiDocs.Spec);
        var operation = document.RootElement.GetProperty("paths").GetProperty(route).GetProperty("get");

        Assert.True(operation.TryGetProperty("security", out var security),
            $"{route} inherits the global Bearer requirement but enforces none");
        Assert.Empty(security.EnumerateArray());
    }

    [Fact]
    public async Task Docs_are_served_on_both_listeners()
    {
        foreach (var client in (HttpClient[])[_pub, _int])
        {
            var page = await client.GetAsync("/docs/");
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
            Assert.Contains("swagger-ui", await page.Content.ReadAsStringAsync());

            var spec = await client.GetAsync("/docs/openapi.json");
            Assert.Equal(HttpStatusCode.OK, spec.StatusCode);
            Assert.Equal(OpenApiDocs.Spec, await spec.Content.ReadAsStringAsync());
        }
    }

    /// <summary>
    /// Routing cannot tell /docs from /docs/, but the browser can: the page's
    /// relative URLs only resolve under the trailing slash, so /docs must
    /// redirect rather than serve a page whose spec fetch 404s.
    /// </summary>
    [Fact]
    public async Task Docs_without_a_trailing_slash_redirect_to_the_canonical_url()
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { BaseAddress = _api.PublicBaseUri };

        var response = await client.GetAsync("/docs");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // relative on purpose: a path-prefixed proxy never shows us its prefix
        Assert.Equal("docs/", response.Headers.Location?.OriginalString);
        Assert.Equal(new Uri(_api.PublicBaseUri, "/docs/"), new Uri(_api.PublicBaseUri, response.Headers.Location!));
    }

    /// <summary>
    /// The page's own relative URL — resolved the way a browser at /docs/ would —
    /// has to land on the spec. This is what the trailing-slash redirect buys.
    /// </summary>
    [Fact]
    public async Task Docs_page_finds_its_spec_where_it_asks_for_it()
    {
        var html = await _pub.GetStringAsync("/docs/");
        Assert.Contains("url: 'openapi.json'", html);

        var resolved = new Uri(new Uri(_pub.BaseAddress!, "/docs/"), "openapi.json");
        Assert.Equal(HttpStatusCode.OK, (await _pub.GetAsync(resolved)).StatusCode);
    }

    /// <summary>
    /// swagger-ui is loaded from unpkg, so a hijacked or substituted response
    /// would run on the API's own origin. Every external tag must therefore
    /// carry an integrity hash and the crossorigin attribute SRI needs, and the
    /// CSP must not admit any host beyond unpkg.
    /// </summary>
    [Fact]
    public async Task External_swagger_ui_tags_are_pinned_by_integrity_hash()
    {
        var html = await _pub.GetStringAsync("/docs/");

        // whole tags, not lines — the attributes are wrapped
        var external = Regex.Matches(html, @"<(?:link|script)\b[^>]*https://[^>]*>")
            .Select(match => match.Value)
            .ToArray();

        Assert.Equal(2, external.Length); // the stylesheet and the bundle
        foreach (var tag in external)
        {
            Assert.Contains("integrity=\"sha384-", tag);
            Assert.Contains("crossorigin=\"anonymous\"", tag);
            // pinned, never @latest — and naming the version here on purpose, so
            // that bumping it forces a look at the hashes it has to agree with
            Assert.Contains("swagger-ui-dist@5.18.2/", tag);
        }

        // unpkg is the only origin the page may reach; everything else is 'self'
        Assert.Contains("default-src 'self'", html);
        Assert.All(
            html.Split("https://").Skip(1)
                .Select(rest => rest.Split(['/', ';', ' ', '"', '\'', '\n'])[0]),
            host => Assert.Equal("unpkg.com", host));
    }

    [Fact]
    public async Task Docs_responses_are_cacheable_and_answer_head()
    {
        var response = await _pub.GetAsync("/docs/openapi.json");
        Assert.Equal("public, max-age=300", response.Headers.CacheControl?.ToString());

        using var head = new HttpRequestMessage(HttpMethod.Head, "/docs/openapi.json");
        Assert.Equal(HttpStatusCode.OK, (await _pub.SendAsync(head)).StatusCode);
    }

    private static IEnumerable<JsonElement> Operations(JsonElement root)
        => root.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(member => HttpMethods.Contains(member.Name))
                .Select(member => member.Value));

    private static IEnumerable<string> References(JsonElement element)
    {
        const string prefix = "#/components/schemas/";
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("$ref") && property.Value.GetString() is { } reference)
                {
                    Assert.StartsWith(prefix, reference);
                    yield return reference[prefix.Length..];
                }
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

/// <summary>
/// EP_DOCS decides who may read the route inventory. The spec names every
/// /internal/* endpoint the app maps, so an internet-facing deployment can keep
/// the page on the internal listener, or drop it entirely.
/// </summary>
public class OpenApiVisibilityTests
{
    [Theory]
    [InlineData("both", HttpStatusCode.OK, HttpStatusCode.OK)]
    [InlineData("internal", HttpStatusCode.NotFound, HttpStatusCode.OK)]
    [InlineData("off", HttpStatusCode.NotFound, HttpStatusCode.NotFound)]
    public async Task Ep_docs_decides_which_listeners_answer(
        string mode, HttpStatusCode onPublic, HttpStatusCode onInternal)
    {
        await using var ds = DocsHost.DataSource();
        await using var api = await DocsHost.StartAsync(ds, mode);
        using var pub = new HttpClient { BaseAddress = api.PublicBaseUri };
        using var @internal = new HttpClient { BaseAddress = api.InternalBaseUri };

        Assert.Equal(onPublic, (await pub.GetAsync("/docs/")).StatusCode);
        Assert.Equal(onPublic, (await pub.GetAsync("/docs/openapi.json")).StatusCode);
        Assert.Equal(onInternal, (await @internal.GetAsync("/docs/")).StatusCode);
        Assert.Equal(onInternal, (await @internal.GetAsync("/docs/openapi.json")).StatusCode);
    }
}

/// <summary>Starts an API for the docs tests. The data source is never opened.</summary>
internal static class DocsHost
{
    public static NpgsqlDataSource DataSource()
        => NpgsqlDataSource.Create("Host=127.0.0.1;Username=none;Database=none");

    public static Task<RunningApi> StartAsync(NpgsqlDataSource ds, string docs = "both")
        => ApiApp.StartAsync(new EpConfig
        {
            DbConnString = "unused-in-tests",
            Listen = "http://127.0.0.1:0",
            InternalListen = "http://127.0.0.1:0",
            Docs = docs,
        }, ds, TenantRegistry.ForTesting(new TenantConfig
        {
            AppId = "zainmart",
            TenantApiKey = "client-key",
            InternalToken = "internal-secret",
            Plan = TrackingPlan.Parse("""{"events":{}}"""),
        }), new MetricsRegistry());
}
