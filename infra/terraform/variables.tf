variable "resource_group_name" {
  type        = string
  description = "Azure resource group name."
  default     = "rg-support-poc-tf"
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
  default     = "supportpoc"
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
  description = "Knowledge index name used by Knowledge Service."
  default     = "knowledge-documents"
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

variable "write_local_config" {
  type        = bool
  description = "Write ../../config/azure.local.json from Terraform outputs."
  default     = true
}

variable "tags" {
  type        = map(string)
  description = "Tags applied to Azure resources."
  default = {
    app         = "support-poc"
    managed_by  = "terraform"
    environment = "poc"
  }
}
