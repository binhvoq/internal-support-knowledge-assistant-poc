output "resource_group_name" {
  value = azurerm_resource_group.poc.name
}

output "storage_account_name" {
  value = azurerm_storage_account.docs.name
}

output "service_bus_namespace_name" {
  value = azurerm_servicebus_namespace.bus.name
}

output "service_bus_topic_name" {
  value = azurerm_servicebus_topic.events.name
}

output "search_service_name" {
  value = azurerm_search_service.knowledge.name
}

output "search_endpoint" {
  value = "https://${azurerm_search_service.knowledge.name}.search.windows.net"
}

output "openai_embedding_endpoint" {
  value = azurerm_cognitive_account.openai_embed.endpoint
}

output "openai_chat_endpoint" {
  value = azurerm_cognitive_account.openai_chat.endpoint
}

output "application_insights_connection_string" {
  value     = azurerm_application_insights.poc.connection_string
  sensitive = true
}

output "azure_local_config_path" {
  value = local.config_path
}

output "entra_tenant_id" {
  description = "Microsoft Entra tenant ID used for JWT validation and MSAL."
  value       = local.entra_enabled ? local.entra_config.tenantId : null
}

output "entra_api_audience" {
  description = "JWT audience / Application ID URI for Support PoC API."
  value       = local.entra_api_audience
}

output "entra_spa_client_id" {
  description = "Public client ID for React SPA (MSAL)."
  value       = local.entra_enabled ? azuread_application.spa[0].client_id : null
}

output "entra_api_client_id" {
  description = "Application (client) ID for Support PoC API resource server."
  value       = local.entra_enabled ? azuread_application.api[0].client_id : null
}

output "entra_mcp_client_id" {
  description = "Client ID for MCP / service-to-service (client credentials)."
  value       = local.entra_enabled ? azuread_application.mcp_service[0].client_id : null
  sensitive   = false
}

output "entra_scope_access_as_user" {
  description = "Delegated API scope for SPA (MSAL scope)."
  value       = local.entra_scope_access_as_user
}

output "entra_app_roles" {
  description = "App roles defined on the API application (for authorization policies)."
  value = local.entra_enabled ? {
    for k, r in local.entra_app_roles : r.value => {
      displayName = r.display_name
      description = r.description
      memberTypes = r.allowed_member_types
    }
  } : null
}
