using Microsoft.AspNetCore.HttpOverrides;
using Yarp.ReverseProxy.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddApplicationInsightsTelemetry();

var frontend = builder.Configuration["Services:Frontend"] ?? "http://localhost:8080";
var tickets = builder.Configuration["Services:Tickets"] ?? "http://localhost:5001";
var knowledge = builder.Configuration["Services:Knowledge"] ?? "http://localhost:5002";
var ai = builder.Configuration["Services:Ai"] ?? "http://localhost:5003";

builder.Services.AddReverseProxy().LoadFromMemory(
[
    new RouteConfig
    {
        RouteId = "ticket-root",
        ClusterId = "tickets",
        Order = 0,
        Match = new RouteMatch { Path = "/api/tickets" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/tickets" }]
    },
    new RouteConfig
    {
        RouteId = "ticket-wildcard",
        ClusterId = "tickets",
        Order = 0,
        Match = new RouteMatch { Path = "/api/tickets/{**remainder}" },
        Transforms = [new Dictionary<string, string> { ["PathPattern"] = "/tickets/{**remainder}" }]
    },
    new RouteConfig
    {
        RouteId = "knowledge-documents-root",
        ClusterId = "knowledge",
        Order = 0,
        Match = new RouteMatch { Path = "/api/knowledge/documents" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/documents" }]
    },
    new RouteConfig
    {
        RouteId = "knowledge-documents-wildcard",
        ClusterId = "knowledge",
        Order = 0,
        Match = new RouteMatch { Path = "/api/knowledge/documents/{**remainder}" },
        Transforms = [new Dictionary<string, string> { ["PathPattern"] = "/documents/{**remainder}" }]
    },
    new RouteConfig
    {
        RouteId = "knowledge-search",
        ClusterId = "knowledge",
        Order = 0,
        Match = new RouteMatch { Path = "/api/knowledge/search" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/search" }]
    },
    new RouteConfig
    {
        RouteId = "ai-chat",
        ClusterId = "ai",
        Order = 0,
        Match = new RouteMatch { Path = "/api/ai/chat" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/ai/chat" }]
    },
    new RouteConfig
    {
        RouteId = "ai-auto-suggestion",
        ClusterId = "ai",
        Order = 0,
        Match = new RouteMatch { Path = "/api/ai/tickets/{**remainder}" },
        Transforms = [new Dictionary<string, string> { ["PathPattern"] = "/tickets/{**remainder}" }]
    },
    new RouteConfig
    {
        RouteId = "ticket-health",
        ClusterId = "tickets",
        Order = 0,
        Match = new RouteMatch { Path = "/api/tickets/health" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/health" }]
    },
    new RouteConfig
    {
        RouteId = "knowledge-health",
        ClusterId = "knowledge",
        Order = 0,
        Match = new RouteMatch { Path = "/api/knowledge/health" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/health" }]
    },
    new RouteConfig
    {
        RouteId = "ai-health",
        ClusterId = "ai",
        Order = 0,
        Match = new RouteMatch { Path = "/api/ai/health" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/health" }]
    },
    new RouteConfig
    {
        RouteId = "ai-chat-ready",
        ClusterId = "ai",
        Order = 0,
        Match = new RouteMatch { Path = "/api/ai/debug/chat-ready" },
        Transforms = [new Dictionary<string, string> { ["PathSet"] = "/debug/chat-ready" }]
    },
    new RouteConfig
    {
        RouteId = "frontend-assets",
        ClusterId = "frontend",
        Order = 100,
        Match = new RouteMatch { Path = "/{**catch-all}" }
    }
],
[
    new ClusterConfig
    {
        ClusterId = "frontend",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["frontend"] = new() { Address = frontend }
        }
    },
    new ClusterConfig
    {
        ClusterId = "tickets",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["tickets"] = new() { Address = tickets }
        }
    },
    new ClusterConfig
    {
        ClusterId = "knowledge",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["knowledge"] = new() { Address = knowledge }
        }
    },
    new ClusterConfig
    {
        ClusterId = "ai",
        Destinations = new Dictionary<string, DestinationConfig>
        {
            ["ai"] = new() { Address = ai }
        }
    }
]);

var app = builder.Build();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var path = context.Request.Path.Value ?? string.Empty;
        if (path is "/" or "/index.html" or "/config.js")
        {
            context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
        }

        return Task.CompletedTask;
    });

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "gateway" })).AllowAnonymous();
app.MapGet("/ready", () => Results.Ok(new { ready = true, service = "gateway" })).AllowAnonymous();

app.MapReverseProxy(proxyPipeline =>
{
    proxyPipeline.Use(async (context, next) =>
    {
        if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            context.Request.Headers.Authorization = authorization;
        }

        await next();
    });

    proxyPipeline.Use(async (context, next) =>
    {
        await next();
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Gateway");
        logger.LogInformation("route={Route} status={Status}", context.Request.Path, context.Response.StatusCode);
    });
});

app.Run();
