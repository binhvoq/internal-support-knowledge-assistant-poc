variable "resource_group_name" {
  type        = string
  description = "Azure resource group name"
  default     = "rg-support-poc"
}

variable "location" {
  type        = string
  description = "Azure region"
  default     = "southeastasia"
}

variable "prefix" {
  type        = string
  description = "Prefix for resource names"
  default     = "supportpoc"
}
