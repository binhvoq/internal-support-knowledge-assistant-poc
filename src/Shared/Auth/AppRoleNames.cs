namespace SupportPoc.Shared.Auth;

/// <summary>Entra app roles on Internal Support API — keep in sync with infra/terraform/identity.tf.</summary>
public static class AppRoleNames
{
    public const string Employee = "Support.Employee";
    public const string Agent = "Support.Agent";
    public const string KnowledgeAdmin = "Support.KnowledgeAdmin";
    public const string Service = "Support.Service";

    public static readonly string[] UserRoles = [Employee, Agent, KnowledgeAdmin];
}
