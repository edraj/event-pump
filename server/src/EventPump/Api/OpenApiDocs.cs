using System.Reflection;

namespace EventPump.Api;
public static class OpenApiDocs
{
    public static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/docs/openapi.json", (RequestDelegate)(context =>
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            return context.Response.WriteAsync(Spec);
        }));

        app.MapGet("/docs", (RequestDelegate)(context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            return context.Response.WriteAsync(SwaggerUiHtml);
        }));
    }

    public static string Spec { get; } = ReadEmbeddedSpec();

    private static string ReadEmbeddedSpec()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("openapi.json")
            ?? throw new InvalidOperationException("openapi.json is not embedded in the assembly");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private const string SwaggerUiHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
            <meta charset="UTF-8">
            <title>Event Pump API</title>
            <meta http-equiv="Content-Security-Policy" content="default-src 'self'; style-src 'self' 'unsafe-inline' https://unpkg.com; script-src 'self' 'unsafe-inline' https://unpkg.com; img-src 'self' data:;" />
            <link rel="stylesheet" href="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui.css" />
        </head>
        <body>
            <div id="swagger-ui"></div>
            <script src="https://unpkg.com/swagger-ui-dist@5.18.2/swagger-ui-bundle.js"></script>
            <script>
                SwaggerUIBundle({
                    url: 'docs/openapi.json',
                    dom_id: '#swagger-ui',
                    layout: 'BaseLayout',
                    deepLinking: true,
                    displayRequestDuration: true,
                    filter: true,
                    tryItOutEnabled: true,
                });
            </script>
        </body>
        </html>
        """;
}
