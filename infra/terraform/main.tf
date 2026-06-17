terraform {
  required_version = ">= 1.5.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
    local = {
      source  = "hashicorp/local"
      version = "~> 2.5"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.12"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {
    resource_group {
      prevent_deletion_if_contains_resources = false
    }
  }
}

provider "azuread" {
  # Empty tenant_id => use tenant from `az login`. Set var.tenant_id for explicit tenant.
  tenant_id = var.tenant_id != "" ? var.tenant_id : null
}

locals {
  name_prefix                   = lower(replace(var.prefix, "-", ""))
  suffix                        = lower(replace(var.suffix, "-", ""))
  frontend_name                 = "${var.prefix}-web-${var.suffix}"
  ticket_service_name           = "${var.prefix}-ticket-${var.suffix}"
  knowledge_service_name        = "${var.prefix}-knowledge-${var.suffix}"
  ai_orchestrator_name          = "${var.prefix}-ai-${var.suffix}"
  gateway_name                  = "${var.prefix}-gateway-${var.suffix}"
  storage_account_name          = substr("${local.name_prefix}store${local.suffix}", 0, 24)
  service_bus_name              = "${var.prefix}-bus-${var.suffix}"
  search_name                   = "${var.prefix}-search-${var.suffix}"
  openai_embed_name             = "${var.prefix}-oai-embed-${var.suffix}"
  openai_chat_name              = "${var.prefix}-oai-chat-${var.suffix}"
  acr_name                      = substr(lower(replace("${var.prefix}${var.suffix}acr", "-", "")), 0, 50)
  container_apps_env            = "${var.prefix}-cae-${var.suffix}"
  container_apps_default_domain = azurerm_container_app_environment.main.default_domain
  config_path                   = abspath("${path.module}/../../config/azure.local.json")
  gateway_frontend_url          = "https://${local.frontend_name}.internal.${local.container_apps_default_domain}"
  gateway_public_url            = "https://${local.gateway_name}.${local.container_apps_default_domain}/"
  spa_redirect_uris_effective   = distinct(concat(var.spa_redirect_uris, [local.gateway_public_url]))

  azure_local_config = merge({
    resourceGroup = azurerm_resource_group.main.name
    location      = azurerm_resource_group.main.location
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
    applicationInsights = {
      connectionString = azurerm_application_insights.main.connection_string
    }
  }, local.entra_config != null ? { entra = local.entra_config } : {})
}

resource "azurerm_resource_group" "main" {
  name     = var.resource_group_name
  location = var.location
  tags     = var.tags
}

resource "azurerm_storage_account" "docs" {
  name                     = local.storage_account_name
  resource_group_name      = azurerm_resource_group.main.name
  location                 = azurerm_resource_group.main.location
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
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = var.service_bus_sku
  tags                = var.tags
}

resource "azurerm_servicebus_namespace_authorization_rule" "app" {
  name         = "support-app"
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
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = var.search_sku
  replica_count       = 1
  partition_count     = 1
  tags                = var.tags
}

resource "azurerm_cognitive_account" "openai_embed" {
  name                = local.openai_embed_name
  location            = var.embedding_location
  resource_group_name = azurerm_resource_group.main.name
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
  resource_group_name = azurerm_resource_group.main.name
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

resource "azurerm_log_analytics_workspace" "main" {
  name                = "${var.prefix}-logs-${var.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  tags                = var.tags
}

resource "azurerm_application_insights" "main" {
  name                = "${var.prefix}-insights-${var.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  workspace_id        = azurerm_log_analytics_workspace.main.id
  application_type    = "web"
  tags                = var.tags
}

resource "random_password" "sql_admin" {
  length  = 24
  special = true
}

resource "azurerm_mssql_server" "main" {
  name                          = "${var.prefix}-sql-${var.suffix}"
  resource_group_name           = azurerm_resource_group.main.name
  location                      = azurerm_resource_group.main.location
  version                       = "12.0"
  administrator_login           = var.sql_admin_login
  administrator_login_password  = random_password.sql_admin.result
  minimum_tls_version           = "1.2"
  public_network_access_enabled = true
  tags                          = var.tags
}

resource "azurerm_mssql_firewall_rule" "allow_azure" {
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.main.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_mssql_database" "tickets" {
  name      = "supportpoc_tickets"
  server_id = azurerm_mssql_server.main.id
  sku_name  = var.sql_database_sku
  tags      = var.tags
}

resource "azurerm_mssql_database" "knowledge" {
  name      = "supportpoc_knowledge"
  server_id = azurerm_mssql_server.main.id
  sku_name  = var.sql_database_sku
  tags      = var.tags
}

resource "azurerm_mssql_database" "orchestrator" {
  name      = "supportpoc_orchestrator"
  server_id = azurerm_mssql_server.main.id
  sku_name  = var.sql_database_sku
  tags      = var.tags
}

resource "azurerm_container_registry" "main" {
  name                = local.acr_name
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  sku                 = "Basic"
  admin_enabled       = true
  tags                = var.tags
}

resource "azurerm_log_analytics_workspace" "containerapps" {
  name                = "${var.prefix}-ca-logs-${var.suffix}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  tags                = var.tags
}

resource "azurerm_container_app_environment" "main" {
  name                       = local.container_apps_env
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.containerapps.id
  tags                       = var.tags
}

resource "azurerm_user_assigned_identity" "containerapps" {
  name                = "${var.prefix}-ca-id-${var.suffix}"
  resource_group_name = azurerm_resource_group.main.name
  location            = azurerm_resource_group.main.location
  tags                = var.tags
}

resource "azurerm_role_assignment" "containerapps_acr_pull" {
  scope                = azurerm_container_registry.main.id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.containerapps.principal_id
}

resource "azurerm_role_assignment" "containerapps_sql" {
  scope                = azurerm_mssql_server.main.id
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.containerapps.principal_id
}

resource "azurerm_container_app" "ticket_service" {
  name                         = local.ticket_service_name
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.containerapps.id]
  }

  registry {
    server               = azurerm_container_registry.main.login_server
    username             = azurerm_container_registry.main.admin_username
    password_secret_name = "acr-pwd"
  }

  secret {
    name  = "acr-pwd"
    value = azurerm_container_registry.main.admin_password
  }

  template {
    min_replicas = 1
    max_replicas = 1
    container {
      name   = "ticket-service"
      image  = "${azurerm_container_registry.main.login_server}/ticket-service@${var.ticket_service_image_digest}"
      cpu    = 0.5
      memory = "1Gi"
      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = "Production"
      }
      env {
        name  = "ConnectionStrings__Tickets"
        value = "Server=tcp:${azurerm_mssql_server.main.fully_qualified_domain_name},1433;Initial Catalog=${azurerm_mssql_database.tickets.name};Persist Security Info=False;User ID=${var.sql_admin_login};Password=${random_password.sql_admin.result};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
      }
      env {
        name  = "ServiceBus__ConnectionString"
        value = azurerm_servicebus_namespace_authorization_rule.app.primary_connection_string
      }
      env {
        name  = "ServiceBus__TopicName"
        value = azurerm_servicebus_topic.events.name
      }
      env {
        name  = "ApplicationInsights__ConnectionString"
        value = azurerm_application_insights.main.connection_string
      }
      env {
        name  = "AzureAd__Enabled"
        value = tostring(local.entra_enabled)
      }
      env {
        name  = "AzureAd__TenantId"
        value = local.entra_enabled ? local.entra_config.tenantId : ""
      }
      env {
        name  = "AzureAd__ClientId"
        value = local.entra_enabled ? local.entra_config.api.clientId : ""
      }
      env {
        name  = "AzureAd__Audience"
        value = local.entra_enabled ? local.entra_config.api.audience : ""
      }
      env {
        name  = "AzureAd__Scope"
        value = local.entra_enabled ? local.entra_config.api.scopeFull : ""
      }
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_role_assignment.containerapps_acr_pull]
}

resource "azurerm_container_app" "knowledge_service" {
  name                         = local.knowledge_service_name
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.containerapps.id]
  }

  registry {
    server               = azurerm_container_registry.main.login_server
    username             = azurerm_container_registry.main.admin_username
    password_secret_name = "acr-pwd"
  }

  secret {
    name  = "acr-pwd"
    value = azurerm_container_registry.main.admin_password
  }

  template {
    min_replicas = 1
    max_replicas = 1
    container {
      name   = "knowledge-service"
      image  = "${azurerm_container_registry.main.login_server}/knowledge-service@${var.knowledge_service_image_digest}"
      cpu    = 0.5
      memory = "1Gi"
      env {
        name  = "ConnectionStrings__Knowledge"
        value = "Server=tcp:${azurerm_mssql_server.main.fully_qualified_domain_name},1433;Initial Catalog=${azurerm_mssql_database.knowledge.name};Persist Security Info=False;User ID=${var.sql_admin_login};Password=${random_password.sql_admin.result};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
      }
      env {
        name  = "AzureSearch__Endpoint"
        value = "https://${azurerm_search_service.knowledge.name}.search.windows.net"
      }
      env {
        name  = "AzureSearch__ApiKey"
        value = azurerm_search_service.knowledge.primary_key
      }
      env {
        name  = "AzureSearch__IndexName"
        value = var.search_index_name
      }
      env {
        name  = "AzureOpenAI__Endpoint"
        value = azurerm_cognitive_account.openai_embed.endpoint
      }
      env {
        name  = "AzureOpenAI__ApiKey"
        value = azurerm_cognitive_account.openai_embed.primary_access_key
      }
      env {
        name  = "AzureOpenAI__EmbeddingDeployment"
        value = azurerm_cognitive_deployment.embedding.name
      }
      env {
        name  = "AzureStorage__ConnectionString"
        value = azurerm_storage_account.docs.primary_connection_string
      }
      env {
        name  = "ServiceBus__ConnectionString"
        value = azurerm_servicebus_namespace_authorization_rule.app.primary_connection_string
      }
      env {
        name  = "ApplicationInsights__ConnectionString"
        value = azurerm_application_insights.main.connection_string
      }
      env {
        name  = "AzureAd__Enabled"
        value = tostring(local.entra_enabled)
      }
      env {
        name  = "AzureAd__TenantId"
        value = local.entra_enabled ? local.entra_config.tenantId : ""
      }
      env {
        name  = "AzureAd__ClientId"
        value = local.entra_enabled ? local.entra_config.api.clientId : ""
      }
      env {
        name  = "AzureAd__Audience"
        value = local.entra_enabled ? local.entra_config.api.audience : ""
      }
      env {
        name  = "AzureAd__Scope"
        value = local.entra_enabled ? local.entra_config.api.scopeFull : ""
      }
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_role_assignment.containerapps_acr_pull]
}

resource "azurerm_container_app" "ai_orchestrator" {
  name                         = local.ai_orchestrator_name
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.containerapps.id]
  }

  registry {
    server               = azurerm_container_registry.main.login_server
    username             = azurerm_container_registry.main.admin_username
    password_secret_name = "acr-pwd"
  }

  secret {
    name  = "acr-pwd"
    value = azurerm_container_registry.main.admin_password
  }

  template {
    min_replicas = 1
    max_replicas = 1
    container {
      name   = "ai-orchestrator"
      image  = "${azurerm_container_registry.main.login_server}/ai-orchestrator@${var.ai_orchestrator_image_digest}"
      cpu    = 0.5
      memory = "1Gi"
      env {
        name  = "ConnectionStrings__Orchestrator"
        value = "Server=tcp:${azurerm_mssql_server.main.fully_qualified_domain_name},1433;Initial Catalog=${azurerm_mssql_database.orchestrator.name};Persist Security Info=False;User ID=${var.sql_admin_login};Password=${random_password.sql_admin.result};MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
      }
      env {
        name  = "ServiceBus__ConnectionString"
        value = azurerm_servicebus_namespace_authorization_rule.app.primary_connection_string
      }
      env {
        name  = "AzureOpenAI__Endpoint"
        value = azurerm_cognitive_account.openai_chat.endpoint
      }
      env {
        name  = "AzureOpenAI__ApiKey"
        value = azurerm_cognitive_account.openai_chat.primary_access_key
      }
      env {
        name  = "AzureOpenAI__ChatEndpoint"
        value = azurerm_cognitive_account.openai_chat.endpoint
      }
      env {
        name  = "AzureOpenAI__ChatApiKey"
        value = azurerm_cognitive_account.openai_chat.primary_access_key
      }
      env {
        name  = "AzureOpenAI__ChatDeployment"
        value = azurerm_cognitive_deployment.chat.name
      }
      env {
        name  = "AzureOpenAI__ChatEnabled"
        value = "true"
      }
      env {
        name  = "Services__TicketService"
        value = "https://${azurerm_container_app.ticket_service.ingress[0].fqdn}"
      }
      env {
        name  = "Services__KnowledgeService"
        value = "https://${azurerm_container_app.knowledge_service.ingress[0].fqdn}"
      }
      env {
        name  = "ApplicationInsights__ConnectionString"
        value = azurerm_application_insights.main.connection_string
      }
      env {
        name  = "AzureAd__Enabled"
        value = tostring(local.entra_enabled)
      }
      env {
        name  = "AzureAd__TenantId"
        value = local.entra_enabled ? local.entra_config.tenantId : ""
      }
      env {
        name  = "AzureAd__ClientId"
        value = local.entra_enabled ? local.entra_config.api.clientId : ""
      }
      env {
        name  = "AzureAd__Audience"
        value = local.entra_enabled ? local.entra_config.api.audience : ""
      }
      env {
        name  = "AzureAd__Scope"
        value = local.entra_enabled ? local.entra_config.api.scopeFull : ""
      }
      env {
        name  = "AzureAd__McpClientId"
        value = local.entra_enabled ? local.entra_config.mcpService.clientId : ""
      }
      env {
        name  = "AzureAd__McpClientSecret"
        value = local.entra_enabled ? local.entra_config.mcpService.clientSecret : ""
      }
    }
  }

  ingress {
    external_enabled = false
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_role_assignment.containerapps_acr_pull]
}

resource "azurerm_container_app" "frontend" {
  name                         = local.frontend_name
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.containerapps.id]
  }

  registry {
    server               = azurerm_container_registry.main.login_server
    username             = azurerm_container_registry.main.admin_username
    password_secret_name = "acr-pwd"
  }

  secret {
    name  = "acr-pwd"
    value = azurerm_container_registry.main.admin_password
  }

  template {
    min_replicas = 1
    max_replicas = 1
    container {
      name   = "frontend"
      image  = "${azurerm_container_registry.main.login_server}/frontend@${var.frontend_image_digest}"
      cpu    = 0.25
      memory = "0.5Gi"
      env {
        name  = "VITE_AAD_TENANT_ID"
        value = local.entra_enabled ? local.entra_config.tenantId : ""
      }
      env {
        name  = "VITE_AAD_AUTHORITY"
        value = local.entra_enabled ? local.entra_config.authority : ""
      }
      env {
        name  = "VITE_AAD_CLIENT_ID"
        value = local.entra_enabled ? local.entra_config.spa.clientId : ""
      }
      env {
        name  = "VITE_AAD_API_SCOPE"
        value = local.entra_enabled ? local.entra_config.api.scopeFull : ""
      }
    }
  }

  ingress {
    external_enabled = false
    target_port      = 80
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_role_assignment.containerapps_acr_pull]
}

resource "azurerm_container_app" "gateway" {
  name                         = local.gateway_name
  resource_group_name          = azurerm_resource_group.main.name
  container_app_environment_id = azurerm_container_app_environment.main.id
  revision_mode                = "Single"
  tags                         = var.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.containerapps.id]
  }

  registry {
    server               = azurerm_container_registry.main.login_server
    username             = azurerm_container_registry.main.admin_username
    password_secret_name = "acr-pwd"
  }

  secret {
    name  = "acr-pwd"
    value = azurerm_container_registry.main.admin_password
  }

  template {
    min_replicas = 1
    max_replicas = 1
    container {
      name   = "gateway"
      image  = "${azurerm_container_registry.main.login_server}/gateway@${var.gateway_image_digest}"
      cpu    = 0.25
      memory = "0.5Gi"
      env {
        name  = "Services__Frontend"
        value = local.gateway_frontend_url
      }
      env {
        name  = "Services__Tickets"
        value = "https://${azurerm_container_app.ticket_service.name}.internal.${local.container_apps_default_domain}"
      }
      env {
        name  = "Services__Knowledge"
        value = "https://${azurerm_container_app.knowledge_service.name}.internal.${local.container_apps_default_domain}"
      }
      env {
        name  = "Services__Ai"
        value = "https://${azurerm_container_app.ai_orchestrator.name}.internal.${local.container_apps_default_domain}"
      }
      env {
        name  = "ApplicationInsights__ConnectionString"
        value = azurerm_application_insights.main.connection_string
      }
    }
  }

  ingress {
    external_enabled = true
    target_port      = 8080
    transport        = "auto"
    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  depends_on = [azurerm_role_assignment.containerapps_acr_pull]
}

resource "local_sensitive_file" "azure_local_config" {
  count           = var.write_local_config ? 1 : 0
  filename        = local.config_path
  content         = jsonencode(local.azure_local_config)
  file_permission = "0600"
}
