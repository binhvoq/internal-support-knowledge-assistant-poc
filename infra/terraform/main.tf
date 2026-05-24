terraform {
  required_version = ">= 1.5.0"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {}
}

resource "azurerm_resource_group" "poc" {
  name     = var.resource_group_name
  location = var.location
}

resource "azurerm_storage_account" "docs" {
  name                     = replace("${var.prefix}store", "-", "")
  resource_group_name      = azurerm_resource_group.poc.name
  location                 = azurerm_resource_group.poc.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
}

resource "azurerm_servicebus_namespace" "bus" {
  name                = "${var.prefix}bus"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  sku                 = "Standard"
}

resource "azurerm_servicebus_topic" "events" {
  name         = "support-events"
  namespace_id = azurerm_servicebus_namespace.bus.id
}

resource "azurerm_servicebus_subscription" "ai_orchestrator" {
  name               = "ai-orchestrator"
  topic_id           = azurerm_servicebus_topic.events.id
  max_delivery_count = 10
}

resource "azurerm_search_service" "search" {
  name                = "${var.prefix}-search"
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  sku                 = "basic"
}

resource "azurerm_cognitive_account" "openai" {
  name                = "${var.prefix}-openai"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  kind                = "OpenAI"
  sku_name            = "S0"
}

resource "azurerm_log_analytics_workspace" "poc" {
  name                = "${var.prefix}-logs"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "poc" {
  name                = "${var.prefix}-insights"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  workspace_id        = azurerm_log_analytics_workspace.poc.id
  application_type    = "web"
}

output "resource_group_name" {
  value = azurerm_resource_group.poc.name
}

output "search_endpoint" {
  value = "https://${azurerm_search_service.search.name}.search.windows.net"
}

output "openai_endpoint" {
  value = azurerm_cognitive_account.openai.endpoint
}

output "application_insights_connection_string" {
  value     = azurerm_application_insights.poc.connection_string
  sensitive = true
}
