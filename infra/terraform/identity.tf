# Microsoft Entra ID — Zero Trust Identity (app roles sync with src/Shared/Auth/AppRoleNames.cs).

data "azuread_client_config" "current" {}

locals {
  entra_enabled = var.enable_entra_identity

  # Stable UUIDs so re-apply does not churn Entra permission IDs.
  entra_api_scope_id = "a8f3c2e1-9b4d-4f6a-bcde-1234567890ab"

  entra_app_roles = {
    employee = {
      id                   = "b1011111-1111-1111-1111-111111111101"
      value                = "Support.Employee"
      display_name         = "Support Employee"
      description          = "Create tickets, use AI chat (read tools), view own tickets."
      allowed_member_types = ["User"]
    }
    agent = {
      id                   = "b1011111-1111-1111-1111-111111111102"
      value                = "Support.Agent"
      display_name         = "Support Agent"
      description          = "Support queue, resolve/reopen tickets, AI suggest."
      allowed_member_types = ["User"]
    }
    knowledge_admin = {
      id                   = "b1011111-1111-1111-1111-111111111103"
      value                = "Support.KnowledgeAdmin"
      display_name         = "Knowledge Admin"
      description          = "Manage knowledge documents and re-index."
      allowed_member_types = ["User"]
    }
    service = {
      id                   = "b1011111-1111-1111-1111-111111111104"
      value                = "Support.Service"
      display_name         = "Support Service"
      description          = "Machine identity for MCP and internal service-to-service API calls."
      allowed_member_types = ["Application"]
    }
  }

  # Set by azuread_application_identifier_uri (tenant policy: URI must include appId).
  entra_api_identifier_uri = local.entra_enabled ? azuread_application_identifier_uri.api[0].identifier_uri : null
  entra_api_audience       = local.entra_api_identifier_uri
  entra_scope_access_as_user = local.entra_enabled ? "${local.entra_api_identifier_uri}/access_as_user" : null

  bootstrap_principals = local.entra_enabled ? (
    var.bootstrap_role_assignments != null ? var.bootstrap_role_assignments : {
      "Support.Employee"       = coalesce(nullif(var.bootstrap_employee_principal_id, ""), var.bootstrap_user_id)
      "Support.Agent"          = coalesce(nullif(var.bootstrap_agent_principal_id, ""), var.bootstrap_user_id)
      "Support.KnowledgeAdmin" = coalesce(nullif(var.bootstrap_knowledge_admin_principal_id, ""), var.bootstrap_user_id)
    }
  ) : {}

  entra_config = local.entra_enabled ? {
    tenantId     = coalesce(var.tenant_id, data.azuread_client_config.current.tenant_id)
    tenantDomain = var.tenant_domain
    api = {
      displayName    = azuread_application.api[0].display_name
      clientId       = azuread_application.api[0].client_id
      applicationId  = azuread_application.api[0].client_id
      audience       = local.entra_api_audience
      identifierUri  = local.entra_api_identifier_uri
      scopeName      = "access_as_user"
      scopeFull      = local.entra_scope_access_as_user
    }
    spa = {
      displayName  = azuread_application.spa[0].display_name
      clientId     = azuread_application.spa[0].client_id
      redirectUris = var.spa_redirect_uris
    }
    mcpService = {
      displayName  = azuread_application.mcp_service[0].display_name
      clientId     = azuread_application.mcp_service[0].client_id
      clientSecret = azuread_application_password.mcp_service[0].value
      secretEndDate = azuread_application_password.mcp_service[0].end_date
    }
    appRoles = {
      for k, r in local.entra_app_roles : r.value => {
        id          = r.id
        displayName = r.display_name
        description = r.description
        memberTypes = r.allowed_member_types
      }
    }
    bootstrapRoleAssignments = local.bootstrap_principals
    bootstrapPrincipals = {
      employee       = local.bootstrap_principals["Support.Employee"]
      agent          = local.bootstrap_principals["Support.Agent"]
      knowledgeAdmin = local.bootstrap_principals["Support.KnowledgeAdmin"]
    }
    authority = "https://login.microsoftonline.com/${coalesce(var.tenant_id, data.azuread_client_config.current.tenant_id)}"
  } : null
}

resource "azuread_application" "api" {
  count = local.entra_enabled ? 1 : 0

  display_name     = "${var.prefix} API (${var.suffix})"
  sign_in_audience = "AzureADMyOrg"
  owners = [data.azuread_client_config.current.object_id]

  lifecycle {
    ignore_changes = [identifier_uris]
  }

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      admin_consent_description  = "Allow Internal Support clients to call APIs on behalf of the signed-in user."
      admin_consent_display_name = "Access Internal Support API as user"
      enabled                    = true
      id                         = local.entra_api_scope_id
      type                       = "User"
      user_consent_description   = "Sign in and use the internal support assistant on your behalf."
      user_consent_display_name  = "Access Internal Support API"
      value                      = "access_as_user"
    }
  }

  dynamic "app_role" {
    for_each = local.entra_app_roles
    content {
      allowed_member_types = app_role.value.allowed_member_types
      description          = app_role.value.description
      display_name         = app_role.value.display_name
      enabled              = true
      id                   = app_role.value.id
      value                = app_role.value.value
    }
  }
}

resource "azuread_application_identifier_uri" "api" {
  count = local.entra_enabled ? 1 : 0

  application_id = azuread_application.api[0].id
  identifier_uri = "api://${azuread_application.api[0].client_id}"
}

resource "azuread_service_principal" "api" {
  count = local.entra_enabled ? 1 : 0

  client_id                    = azuread_application.api[0].client_id
  app_role_assignment_required = false
  owners                       = [data.azuread_client_config.current.object_id]
}

resource "azuread_application" "spa" {
  count = local.entra_enabled ? 1 : 0

  display_name     = "${var.prefix} SPA (${var.suffix})"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azuread_client_config.current.object_id]

  single_page_application {
    redirect_uris = var.spa_redirect_uris
  }

  required_resource_access {
    resource_app_id = azuread_application.api[0].client_id

    resource_access {
      id   = local.entra_api_scope_id
      type = "Scope"
    }
  }
}

resource "azuread_service_principal" "spa" {
  count = local.entra_enabled ? 1 : 0

  client_id = azuread_application.spa[0].client_id
  owners    = [data.azuread_client_config.current.object_id]
}

resource "azuread_application_pre_authorized" "spa" {
  count = local.entra_enabled ? 1 : 0

  application_id       = azuread_application.api[0].id
  authorized_client_id = azuread_application.spa[0].client_id
  permission_ids       = [local.entra_api_scope_id]
}

resource "azuread_application" "mcp_service" {
  count = local.entra_enabled ? 1 : 0

  display_name     = "${var.prefix} MCP Service (${var.suffix})"
  sign_in_audience = "AzureADMyOrg"
  owners           = [data.azuread_client_config.current.object_id]

  required_resource_access {
    resource_app_id = azuread_application.api[0].client_id

    resource_access {
      id   = local.entra_app_roles.service.id
      type = "Role"
    }
  }
}

resource "azuread_service_principal" "mcp_service" {
  count = local.entra_enabled ? 1 : 0

  client_id = azuread_application.mcp_service[0].client_id
  owners    = [data.azuread_client_config.current.object_id]
}

# Stable expiry anchor — avoid timestamp() on every plan (would rotate MCP secret each run).
resource "time_offset" "mcp_secret_expiry" {
  count = local.entra_enabled ? 1 : 0

  offset_days = var.entra_client_secret_days
}

resource "azuread_application_password" "mcp_service" {
  count = local.entra_enabled ? 1 : 0

  application_id = azuread_application.mcp_service[0].id
  display_name   = "terraform-mcp-${var.suffix}"
  end_date       = time_offset.mcp_secret_expiry[0].rfc3339

  lifecycle {
    create_before_destroy = true
  }
}

resource "azuread_app_role_assignment" "mcp_service" {
  count = local.entra_enabled ? 1 : 0

  app_role_id         = azuread_service_principal.api[0].app_role_ids["Support.Service"]
  principal_object_id = azuread_service_principal.mcp_service[0].object_id
  resource_object_id  = azuread_service_principal.api[0].object_id
}

resource "azuread_app_role_assignment" "bootstrap" {
  for_each = local.bootstrap_principals

  app_role_id         = azuread_service_principal.api[0].app_role_ids[each.key]
  principal_object_id = each.value
  resource_object_id  = azuread_service_principal.api[0].object_id
}
