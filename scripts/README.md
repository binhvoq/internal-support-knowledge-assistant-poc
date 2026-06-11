# Scripts

This project intentionally does not keep shell automation scripts here.

AI agents should run the required commands directly and report the exact commands/results instead of creating one-off `.sh` files.

## Local dev (SQL Server + Service Bus emulator)

Requires Docker. Starts SQL Server on `localhost:1433` (`sa` / `SupportPoc_LocalSql1!`) and the Service Bus emulator.

Connection strings are already set in each service `appsettings.json`. For local overrides, copy `appsettings.Development.json.example` → `appsettings.Development.json` in each service project (gitignored).

Override via env only if needed:

```powershell
docker compose -f .emulator/docker-compose.yml up -d
dotnet build SupportPoc.slnx
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:ServiceBus__ConnectionString='Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;'
$env:AzureAd__Enabled='false'
$env:AzureOpenAI__ChatEnabled='false'
dotnet run --project src/TicketService/SupportPoc.TicketService.csproj --no-launch-profile --urls http://localhost:5001
dotnet run --project src/KnowledgeService/SupportPoc.KnowledgeService.csproj --no-launch-profile --urls http://localhost:5002
dotnet run --project src/AiOrchestrator/SupportPoc.AiOrchestrator.csproj --no-launch-profile --urls http://localhost:5003
```

Optional connection string overrides:

```powershell
$env:ConnectionStrings__Tickets='Server=localhost,1433;Database=supportpoc_tickets;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;'
$env:ConnectionStrings__Orchestrator='Server=localhost,1433;Database=supportpoc_orchestrator;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;'
$env:ConnectionStrings__Knowledge='Server=localhost,1433;Database=supportpoc_knowledge;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;'
```

### Smoke verification

```powershell
Invoke-WebRequest http://localhost:5001/ready -UseBasicParsing
Invoke-WebRequest http://localhost:5003/ready -UseBasicParsing
Invoke-RestMethod http://localhost:5001/tickets -Method Post -ContentType 'application/json' -Body '{"employeeId":"e2e","question":"vpn khong ket noi","category":"IT"}'
Invoke-RestMethod 'http://localhost:5003/debug/saga-instances?ticketId=TCK-001'
Invoke-RestMethod http://localhost:5001/debug/outbox
```

Verify saga consumer outbox: `Invoke-RestMethod http://localhost:5003/ready` → `messaging.sagaConsumerOutbox` should be `true`.

If a reusable workflow is needed, prefer documenting the direct command sequence here instead of adding a new script.
