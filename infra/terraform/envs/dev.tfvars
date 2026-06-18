resource_group_name = "rg-internal-support-dev"
location            = "southeastasia"
prefix              = "support"
suffix              = "dev01"

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
  environment = "dev"
}

frontend_image_digest          = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
gateway_image_digest           = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
ticket_service_image_digest    = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
knowledge_service_image_digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
ai_orchestrator_image_digest   = "sha256:0000000000000000000000000000000000000000000000000000000000000000"
