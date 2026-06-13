using System.Security.Claims;

namespace SupportPoc.AiOrchestrator.Services;

internal static class OpsCallerIdentity
{
    internal static string Resolve(ClaimsPrincipal? user, IHostEnvironment environment)
    {
        if (user?.Identity?.IsAuthenticated == true)
        {
            var name = user.Identity.Name;
            if (!string.IsNullOrWhiteSpace(name))
                return name;

            var oid = user.FindFirst("oid")?.Value
                ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(oid))
                return oid;

            return "authenticated";
        }

        return environment.IsDevelopment() ? "anonymous/dev" : "anonymous";
    }
}
