using SupportPoc.McpToolServer.Tools;
using SupportPoc.Shared.Auth;

var builder = WebApplication.CreateBuilder(args);

var entraEnabled = builder.Configuration.IsEntraEnabled();
if (entraEnabled)
{
    builder.Services.AddSupportPocEntraAuth(builder.Configuration);
    builder.Services.AddSupportPocClientCredentials(builder.Configuration);
}

static void AddDownstreamClient(IServiceCollection services, bool entraEnabled, string name, string configKey, string fallback)
{
    var registration = services.AddHttpClient(name, (sp, client) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var baseUrl = config[configKey] ?? fallback;
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
    });
    if (entraEnabled)
        registration.AddHttpMessageHandler<EntraBearerTokenHandler>();
}

AddDownstreamClient(builder.Services, entraEnabled, "ticket-api", "Services:TicketService", "http://localhost:5001");
AddDownstreamClient(builder.Services, entraEnabled, "knowledge-api", "Services:KnowledgeService", "http://localhost:5002");

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<SupportTools>();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
if (entraEnabled)
    app.UseSupportPocEntraAuth();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "mcp-tool-server" }))
    .AllowAnonymous();

app.MapGet("/internal/mcp/tool-policies", () => Results.Ok(SupportToolPolicyCatalog.FromToolType<SupportTools>()))
    .WithEntraPolicy(entraEnabled, PolicyNames.Service);

var mcp = app.MapMcp("/mcp");
if (entraEnabled)
    mcp.RequireAuthorization(PolicyNames.Service);

app.Run();
