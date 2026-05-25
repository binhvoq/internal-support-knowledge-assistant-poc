using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SupportPoc.TicketService.Data;

namespace SupportPoc.TicketService.Services;

internal static class IdempotencyHelper
{
    public static string? ReadKey(HttpContext httpContext) =>
        httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var values)
            ? values.FirstOrDefault()
            : null;

    public static string Hash<T>(string scope, T payload)
    {
        var json = JsonSerializer.Serialize(new { scope, payload }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }

    public static IResult Replay(IdempotencyRecordEntity record) =>
        Results.Text(record.ResponseJson, "application/json", statusCode: record.StatusCode);
}
