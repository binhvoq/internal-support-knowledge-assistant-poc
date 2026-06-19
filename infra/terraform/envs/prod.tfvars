resource_group_name = "rg-internal-support-prod"
location            = "southeastasia"
prefix              = "support"
suffix              = "prod01"

embedding_location = "eastus"
chat_location      = "eastus"

search_sku = "basic"

tenant_id     = "88a56b4b-d214-4a74-bb3d-aacc38429f62"
tenant_domain = "binhthedevgmail.onmicrosoft.com"

spa_redirect_uris = [
  "http://localhost:3000/",
  "http://127.0.0.1:3000/",
  "http://localhost:5173/",
  "http://127.0.0.1:5173/"
]

bootstrap_user_id = "c0656246-0907-4c6f-8871-25b622341cb3"
enable_entra_identity = true

tags = {
  app         = "internal-support"
  managed_by  = "terraform"
  environment = "prod"
}

frontend_image_digest          = null
gateway_image_digest           = null
ticket_service_image_digest    = null
knowledge_service_image_digest = null
ai_orchestrator_image_digest   = null
