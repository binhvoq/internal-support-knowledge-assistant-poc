namespace SupportPoc.Shared.Auth;

public static class PolicyNames
{
    /// <summary>Employee, Agent, or Knowledge Admin (delegated user).</summary>
    public const string EmployeeOrAbove = "Support.EmployeeOrAbove";

    /// <summary>Support queue operations.</summary>
    public const string Agent = "Support.Agent";

    /// <summary>Knowledge document CRUD and re-index.</summary>
    public const string KnowledgeAdmin = "Support.KnowledgeAdmin";

    /// <summary>MCP / orchestrator machine identity.</summary>
    public const string Service = "Support.Service";

    /// <summary>Agent UI or internal service callers.</summary>
    public const string AgentOrService = "Support.AgentOrService";

    /// <summary>Any user app role or machine service identity.</summary>
    public const string UserOrService = "Support.UserOrService";
}
