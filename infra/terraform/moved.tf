# Renamed Terraform addresses without recreating Azure resources.

moved {
  from = azurerm_resource_group.poc
  to   = azurerm_resource_group.main
}

moved {
  from = azurerm_log_analytics_workspace.poc
  to   = azurerm_log_analytics_workspace.main
}

moved {
  from = azurerm_application_insights.poc
  to   = azurerm_application_insights.main
}
