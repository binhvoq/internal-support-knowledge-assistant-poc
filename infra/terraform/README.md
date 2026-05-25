# Terraform

Terraform is the preferred long-term path for starting/stopping Azure resources for this PoC. The legacy scripts in `../../scripts` are still kept while Terraform stabilizes.

## What It Creates

- Resource Group
- Storage Account + `knowledge-docs` container
- Service Bus namespace + `support-events` topic + `ai-orchestrator` subscription
- Azure AI Search
- Azure OpenAI embedding account + `text-embedding-3-small` deployment
- Azure OpenAI chat account + `gpt-4.1-mini` deployment
- Log Analytics + Application Insights
- Local generated config: `../../config/azure.local.json`

## Start

```bash
cd infra/terraform
terraform init
terraform apply
```

After apply:

```bash
cd ../..
bash scripts/sync-config.sh
bash scripts/restart-services.sh
bash scripts/smoke-test.sh
```

`terraform apply` writes `config/azure.local.json` by default. Set `write_local_config = false` if you only want Azure resources and no local file write.

## Stop

```bash
cd infra/terraform
terraform destroy
```

Azure Search Basic and Service Bus Standard have fixed monthly cost, so destroy resources when the PoC is not being used.

## Naming

Defaults use `rg-support-poc-tf` and suffix `tf01` so Terraform-managed resources do not collide with the older script-managed `rg-support-poc` resources. If a globally unique name collides, change `suffix`.
