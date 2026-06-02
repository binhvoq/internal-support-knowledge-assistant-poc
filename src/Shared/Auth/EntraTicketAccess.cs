using System.Security.Claims;

namespace SupportPoc.Shared.Auth;

/// <summary>Kiem tra quyen doc ticket theo Entra oid (Zero Trust — khong tin employeeId tu client).</summary>
public static class EntraTicketAccess
{
    public static string? GetUserOid(ClaimsPrincipal user) =>
        user.FindFirstValue("oid") ?? user.FindFirstValue(ClaimTypes.NameIdentifier);

    public static bool IsPrivilegedReader(ClaimsPrincipal user) =>
        user.IsInRole(AppRoleNames.Service)
        || user.IsInRole(AppRoleNames.Agent)
        || user.IsInRole(AppRoleNames.KnowledgeAdmin);

    public static bool CanReadTicket(string? ticketOwnerOid, string ticketEmployeeId, ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
            return false;
        if (IsPrivilegedReader(user))
            return true;

        var oid = GetUserOid(user);
        if (!string.IsNullOrWhiteSpace(ticketOwnerOid) && !string.IsNullOrWhiteSpace(oid))
            return string.Equals(ticketOwnerOid, oid, StringComparison.OrdinalIgnoreCase);

        var username = user.FindFirstValue("preferred_username") ?? user.Identity?.Name;
        return !string.IsNullOrWhiteSpace(username)
            && string.Equals(ticketEmployeeId, username, StringComparison.OrdinalIgnoreCase);
    }
}
