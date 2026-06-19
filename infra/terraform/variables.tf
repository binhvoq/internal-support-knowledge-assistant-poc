variable "resource_group_name" {
  type        = string
  description = "Azure resource group name."
  default     = "rg-internal-support-tf"
}

variable "location" {
  type        = string
  description = "Primary Azure region for Storage, Service Bus and AI Search."
  default     = "southeastasia"
}

variable "embedding_location" {
  type        = string
  description = "Azure OpenAI region for embedding deployment."
  default     = "eastus"
}

variable "chat_location" {
  type        = string
  description = "Azure OpenAI region for chat deployment."
  default     = "eastus"
}

variable "prefix" {
  type        = string
  description = "Stable prefix for resource names."
  default     = "support"
}

variable "suffix" {
  type        = string
  description = "Stable suffix for resource names. Change this only when names collide globally."
  default     = "tf01"
}

variable "service_bus_sku" {
  type        = string
  description = "Service Bus SKU. Standard is used because topics/subscriptions are required."
  default     = "Standard"
}

variable "service_bus_topic_name" {
  type        = string
  description = "Service Bus topic used by the event-driven flow."
  default     = "support-events"
}

variable "ai_orchestrator_subscription_name" {
  type        = string
  description = "Subscription consumed by AI Orchestrator."
  default     = "ai-orchestrator"
}

variable "search_sku" {
  type        = string
  description = "Azure AI Search SKU."
  default     = "basic"
}

variable "search_index_name" {
  type        = string
  description = "Knowledge chunk index name used by Knowledge Service."
  default     = "knowledge-chunks"
}

variable "embedding_deployment_name" {
  type        = string
  description = "Azure OpenAI embedding deployment name."
  default     = "text-embedding-3-small"
}

variable "embedding_model_name" {
  type        = string
  description = "Azure OpenAI embedding model name."
  default     = "text-embedding-3-small"
}

variable "embedding_model_version" {
  type        = string
  description = "Azure OpenAI embedding model version."
  default     = "1"
}

variable "embedding_capacity" {
  type        = number
  description = "Embedding deployment TPM capacity."
  default     = 10
}

variable "chat_deployment_name" {
  type        = string
  description = "Azure OpenAI chat deployment name used by the app."
  default     = "gpt-4.1-mini"
}

variable "chat_model_name" {
  type        = string
  description = "Azure OpenAI chat model name."
  default     = "gpt-4.1-mini"
}

variable "chat_model_version" {
  type        = string
  description = "Azure OpenAI chat model version."
  default     = "2025-04-14"
}

variable "chat_capacity" {
  type        = number
  description = "Chat deployment TPM capacity."
  default     = 10
}

variable "log_retention_days" {
  type        = number
  description = "Log Analytics retention in days."
  default     = 30
}

variable "sql_admin_login" {
  type        = string
  description = "SQL Server administrator login for Azure SQL."
  default     = "supportadmin"
}

variable "sql_database_sku" {
  type        = string
  description = "Azure SQL database SKU name."
  default     = "Basic"
}

variable "frontend_image_digest" {
  type        = string
  description = "Immutable digest for the frontend image."
  default     = null
  nullable    = true
  validation {
    condition = (
      var.frontend_image_digest == null
      || (
        can(regex("^sha256:[0-9a-f]{64}$", var.frontend_image_digest))
        && !can(regex("^sha256:0{64}$", var.frontend_image_digest))
      )
    )
    error_message = "frontend_image_digest phai la sha256 digest hop le hoac null khi bootstrap."
  }
}

variable "gateway_image_digest" {
  type        = string
  description = "Immutable digest for the gateway image."
  default     = null
  nullable    = true
  validation {
    condition = (
      var.gateway_image_digest == null
      || (
        can(regex("^sha256:[0-9a-f]{64}$", var.gateway_image_digest))
        && !can(regex("^sha256:0{64}$", var.gateway_image_digest))
      )
    )
    error_message = "gateway_image_digest phai la sha256 digest hop le hoac null khi bootstrap."
  }
}

variable "ticket_service_image_digest" {
  type        = string
  description = "Immutable digest for the ticket service image."
  default     = null
  nullable    = true
  validation {
    condition = (
      var.ticket_service_image_digest == null
      || (
        can(regex("^sha256:[0-9a-f]{64}$", var.ticket_service_image_digest))
        && !can(regex("^sha256:0{64}$", var.ticket_service_image_digest))
      )
    )
    error_message = "ticket_service_image_digest phai la sha256 digest hop le hoac null khi bootstrap."
  }
}

variable "knowledge_service_image_digest" {
  type        = string
  description = "Immutable digest for the knowledge service image."
  default     = null
  nullable    = true
  validation {
    condition = (
      var.knowledge_service_image_digest == null
      || (
        can(regex("^sha256:[0-9a-f]{64}$", var.knowledge_service_image_digest))
        && !can(regex("^sha256:0{64}$", var.knowledge_service_image_digest))
      )
    )
    error_message = "knowledge_service_image_digest phai la sha256 digest hop le hoac null khi bootstrap."
  }
}

variable "ai_orchestrator_image_digest" {
  type        = string
  description = "Immutable digest for the AI orchestrator image."
  default     = null
  nullable    = true
  validation {
    condition = (
      var.ai_orchestrator_image_digest == null
      || (
        can(regex("^sha256:[0-9a-f]{64}$", var.ai_orchestrator_image_digest))
        && !can(regex("^sha256:0{64}$", var.ai_orchestrator_image_digest))
      )
    )
    error_message = "ai_orchestrator_image_digest phai la sha256 digest hop le hoac null khi bootstrap."
  }
}

variable "write_local_config" {
  type        = bool
  description = "Write ../../config/azure.local.json from Terraform outputs."
  default     = true
}

variable "tags" {
  type        = map(string)
  description = "Tags applied to Azure resources."
  default = {
    app         = "internal-support"
    managed_by  = "terraform"
    environment = "production"
  }
}

# ---------- Microsoft Entra ID (Zero Trust Identity) ----------

variable "enable_entra_identity" {
  type        = bool
  description = "Provision Entra app registrations, app roles, and MCP service principal via Terraform."
  default     = true
}

variable "tenant_id" {
  type        = string
  description = "Microsoft Entra tenant ID. Empty uses the tenant from az login."
  default     = "88a56b4b-d214-4a74-bb3d-aacc38429f62"
}

variable "tenant_domain" {
  type        = string
  description = "Primary tenant domain (for docs and MSAL authority hints)."
  default     = "binhthedevgmail.onmicrosoft.com"
}

variable "spa_redirect_uris" {
  type        = list(string)
  description = "Redirect URIs for the React SPA (MSAL PKCE)."
  default     = ["http://localhost:3000/", "http://127.0.0.1:3000/", "http://localhost:5173/", "http://127.0.0.1:5173/"]
}

variable "entra_client_secret_days" {
  type        = number
  description = "Validity period for the MCP service client secret created by Terraform."
  default     = 365
}

variable "bootstrap_user_id" {
  type        = string
  description = "Fallback Entra object ID when role-specific bootstrap principal IDs are empty."
  default     = "c0656246-0907-4c6f-8871-25b622341cb3"
}

variable "bootstrap_employee_principal_id" {
  type        = string
  description = "Entra object ID for Support.Employee bootstrap. Empty uses bootstrap_user_id."
  default     = ""
}

variable "bootstrap_agent_principal_id" {
  type        = string
  description = "Entra object ID for Support.Agent bootstrap. Empty uses bootstrap_user_id."
  default     = ""
}

variable "bootstrap_knowledge_admin_principal_id" {
  type        = string
  description = "Entra object ID for Support.KnowledgeAdmin bootstrap. Empty uses bootstrap_user_id."
  default     = ""
}

variable "bootstrap_role_assignments" {
  type        = map(string)
  description = "Deprecated override — prefer bootstrap_*_principal_id. Kept for backward compat."
  default     = null
  nullable    = true
}

variable "allow_bootstrap_multi_role_principal" {
  type        = bool
  description = "Cho phep mot principal giu nhieu app role trong bootstrap (dev / first deploy)."
  default     = true
}
