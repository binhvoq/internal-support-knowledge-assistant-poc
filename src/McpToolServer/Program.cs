using SupportPoc.McpToolServer.Tools;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<SupportTools>();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "mcp-tool-server" }));
app.MapMcp("/mcp");
app.Run();
