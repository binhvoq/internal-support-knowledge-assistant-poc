namespace SupportPoc.Shared.Contracts;

public sealed record McpToolPolicyDto(
    string ToolName,
    string[] AllowedRoles,
    string Risk,
    string Notes);
