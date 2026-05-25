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
