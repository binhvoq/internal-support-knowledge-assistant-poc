terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    local = {
      source  = "hashicorp/local"
      version = "~> 2.5"
    }
  }
}

provider "azurerm" {
  features {}
}

locals {
  name_prefix          = lower(replace(var.prefix, "-", ""))
  suffix               = lower(replace(var.suffix, "-", ""))
  storage_account_name = substr("${local.name_prefix}store${local.suffix}", 0, 24)
  service_bus_name     = "${var.prefix}-bus-${var.suffix}"
  search_name          = "${var.prefix}-search-${var.suffix}"
  openai_embed_name    = "${var.prefix}-oai-embed-${var.suffix}"
  openai_chat_name     = "${var.prefix}-oai-chat-${var.suffix}"
  config_path          = abspath("${path.module}/../../config/azure.local.json")

  azure_local_config = {
    resourceGroup = azurerm_resource_group.poc.name
    location      = azurerm_resource_group.poc.location
    serviceBus = {
      connectionString = azurerm_servicebus_namespace_authorization_rule.app.primary_connection_string
      topicName        = azurerm_servicebus_topic.events.name
    }
    azureSearch = {
      endpoint  = "https://${azurerm_search_service.knowledge.name}.search.windows.net"
      apiKey    = azurerm_search_service.knowledge.primary_key
      indexName = var.search_index_name
    }
    azureOpenAI = {
      endpoint            = azurerm_cognitive_account.openai_embed.endpoint
      apiKey              = azurerm_cognitive_account.openai_embed.primary_access_key
      chatDeployment      = azurerm_cognitive_deployment.chat.name
      embeddingDeployment = azurerm_cognitive_deployment.embedding.name
      chatEndpoint        = azurerm_cognitive_account.openai_chat.endpoint
      chatApiKey          = azurerm_cognitive_account.openai_chat.primary_access_key
    }
    storage = {
      connectionString = azurerm_storage_account.docs.primary_connection_string
    }
  }
}

resource "azurerm_resource_group" "poc" {
  name     = var.resource_group_name
  location = var.location
  tags     = var.tags
}

resource "azurerm_storage_account" "docs" {
  name                     = local.storage_account_name
  resource_group_name      = azurerm_resource_group.poc.name
  location                 = azurerm_resource_group.poc.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
  tags                     = var.tags
}

resource "azurerm_storage_container" "knowledge_docs" {
  name                  = "knowledge-docs"
  storage_account_id    = azurerm_storage_account.docs.id
  container_access_type = "private"
}

resource "azurerm_servicebus_namespace" "bus" {
  name                = local.service_bus_name
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  sku                 = var.service_bus_sku
  tags                = var.tags
}

resource "azurerm_servicebus_namespace_authorization_rule" "app" {
  name         = "support-poc-app"
  namespace_id = azurerm_servicebus_namespace.bus.id

  listen = true
  send   = true
  manage = true
}

resource "azurerm_servicebus_topic" "events" {
  name         = var.service_bus_topic_name
  namespace_id = azurerm_servicebus_namespace.bus.id
}

resource "azurerm_servicebus_subscription" "ai_orchestrator" {
  name               = var.ai_orchestrator_subscription_name
  topic_id           = azurerm_servicebus_topic.events.id
  max_delivery_count = 10
}

resource "azurerm_search_service" "knowledge" {
  name                = local.search_name
  resource_group_name = azurerm_resource_group.poc.name
  location            = azurerm_resource_group.poc.location
  sku                 = var.search_sku
  replica_count       = 1
  partition_count     = 1
  tags                = var.tags
}

resource "azurerm_cognitive_account" "openai_embed" {
  name                = local.openai_embed_name
  location            = var.embedding_location
  resource_group_name = azurerm_resource_group.poc.name
  kind                = "OpenAI"
  sku_name            = "S0"
  tags                = var.tags
}

resource "azurerm_cognitive_deployment" "embedding" {
  name                 = var.embedding_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai_embed.id

  model {
    format  = "OpenAI"
    name    = var.embedding_model_name
    version = var.embedding_model_version
  }

  sku {
    name     = "Standard"
    capacity = var.embedding_capacity
  }
}

resource "azurerm_cognitive_account" "openai_chat" {
  name                = local.openai_chat_name
  location            = var.chat_location
  resource_group_name = azurerm_resource_group.poc.name
  kind                = "OpenAI"
  sku_name            = "S0"
  tags                = var.tags
}

resource "azurerm_cognitive_deployment" "chat" {
  name                 = var.chat_deployment_name
  cognitive_account_id = azurerm_cognitive_account.openai_chat.id

  model {
    format  = "OpenAI"
    name    = var.chat_model_name
    version = var.chat_model_version
  }

  sku {
    name     = "Standard"
    capacity = var.chat_capacity
  }
}

resource "azurerm_log_analytics_workspace" "poc" {
  name                = "${var.prefix}-logs-${var.suffix}"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  tags                = var.tags
}

resource "azurerm_application_insights" "poc" {
  name                = "${var.prefix}-insights-${var.suffix}"
  location            = azurerm_resource_group.poc.location
  resource_group_name = azurerm_resource_group.poc.name
  workspace_id        = azurerm_log_analytics_workspace.poc.id
  application_type    = "web"
  tags                = var.tags
}

resource "local_sensitive_file" "azure_local_config" {
  count           = var.write_local_config ? 1 : 0
  filename        = local.config_path
  content         = jsonencode(local.azure_local_config)
  file_permission = "0600"
}
