using System.Reflection;

namespace EventPump.Api;

/// <summary>
/// Serves the hand-maintained OpenAPI spec and a Swagger UI page around it. The
/// spec is embedded in the binary; swagger-ui itself comes from unpkg, pinned by
/// version and by subresource-integrity hash.
/// </summary>
public static class OpenApiDocs
{
    public static string Spec { get; } = ReadEmbeddedSpec();

    public static void MapEndpoints(WebApplication app)
    {
        // The page lives at /docs/ so its relative spec URL resolves next to it.
        // Routing cannot tell /docs from /docs/, so the handler canonicalises:
        // without the redirect, /docs/ would ask for /docs/docs/openapi.json and
        // the page would come up empty. The Location is relative on purpose — it
        // survives a path-prefixed reverse proxy, whose prefix the app never sees.
        Map(app, "/docs", "text/html; charset=utf-8", context =>
        {
            if (context.Request.Path.Value?.EndsWith('/') != true)
            {
                context.Response.Redirect("docs/");
                return Task.CompletedTask;
            }
            return context.Response.WriteAsync(SwaggerUiHtml, context.RequestAborted);
        });

        Map(app, "/docs/openapi.json", "application/json; charset=utf-8",
            context => context.Response.WriteAsync(Spec, context.RequestAborted));
    }

    private static void Map(WebApplication app, string route, string contentType, RequestDelegate handler)
        => app.MapMethods(route, ["GET", "HEAD"], (RequestDelegate)(context =>
        {
            context.Response.ContentType = contentType;
            context.Response.Headers.CacheControl = "public, max-age=300"; // changes only on redeploy
            return handler(context);
        }));

    private static string ReadEmbeddedSpec()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("openapi.json")
            ?? throw new InvalidOperationException("openapi.json is not embedded in the assembly");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // swagger-ui comes from unpkg, so the CSP has to allow that host — and the
    // integrity hashes are what stop it from being a blank cheque: a substituted
    // or tampered response fails the check and simply does not execute.
    // crossorigin="anonymous" is required for SRI on a cross-origin fetch, and
    // 'unsafe-inline' covers the bootstrap script below plus the styles
    // swagger-ui injects at runtime.
    //
    // To bump the version, recompute both hashes from the exact bytes unpkg
    // serves — a stale hash blocks the resource outright:
    //
    //   curl -sSLf "https://unpkg.com/swagger-ui-dist@$V/swagger-ui-bundle.js" \
    //     | openssl dgst -sha384 -binary | openssl base64 -A
    //
    // Note that /docs needs outbound internet from the *browser*, not from the
    // server; on an isolated network the page loads empty. See EP_DOCS.
    //
    // `validatorUrl: null` in the init block is load-bearing. Left at its
    // default, swagger-ui points it at Swagger's hosted validator and renders a
    // badge whose <img> src carries this deployment's absolute spec URL to that
    // third party. The CSP's `img-src 'self' data:` happens to block the
    // request today, but that also hides it from
    // External_swagger_ui_tags_are_pinned_by_integrity_hash — the test scans
    // this HTML and cannot see a URL baked into the bundle. Relax the CSP, or
    // serve the page anywhere the meta tag is stripped, and an internal
    // hostname leaves the building. Switching the badge off is the real fix.
    private const string SwaggerUiHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Event Pump API</title>
            <meta http-equiv="Content-Security-Policy" content="default-src 'self'; style-src 'self' 'unsafe-inline' https://unpkg.com; script-src 'self' 'unsafe-inline' https://unpkg.com; img-src 'self' data:;" />
            <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui.css"
                  integrity="sha384-rcbEi6xgdPk0iWkAQzT2F3FeBJXdG+ydrawGlfHAFIZG7wU6aKbQaRewysYpmrlW"
                  crossorigin="anonymous" />
        </head>
        <body>
            <div id="swagger-ui"></div>
            <script src="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui-bundle.js"
                    integrity="sha384-NXtFPpN61oWCuN4D42K6Zd5Rt2+uxeIT36R7kpXBuY9tLnZorzrJ4ykpqwJfgjpZ"
                    crossorigin="anonymous"></script>
            <script>
                SwaggerUIBundle({
                    url: 'openapi.json',
                    dom_id: '#swagger-ui',
                    layout: 'BaseLayout',
                    deepLinking: true,
                    displayRequestDuration: true,
                    filter: true,
                    tryItOutEnabled: true,
                    // off; see the note above SwaggerUiHtml
                    validatorUrl: null,
                });
            </script>
        </body>
        </html>
        """;
}
