# Scripts

This project intentionally does not keep shell automation scripts here.

AI agents should run the required commands directly and report the exact commands/results instead of creating one-off `.sh` files.

Common direct commands:

### SQLite (default PoC)

```powershell
docker compose -f .emulator/docker-compose.yml up -d
dotnet build SupportPoc.slnx
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:ServiceBus__ConnectionString='Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;'
$env:AzureAd__Enabled='false'
$env:AzureOpenAI__ChatEnabled='false'
dotnet run --project src/TicketService/SupportPoc.TicketService.csproj --no-launch-profile --urls http://localhost:5001
dotnet run --project src/AiOrchestrator/SupportPoc.AiOrchestrator.csproj --no-launch-profile --urls http://localhost:5003
```

For smoke verification, call the HTTP endpoints directly:

```powershell
Invoke-WebRequest http://localhost:5001/ready -UseBasicParsing
Invoke-WebRequest http://localhost:5003/ready -UseBasicParsing
Invoke-RestMethod http://localhost:5001/tickets -Method Post -ContentType 'application/json' -Body '{"employeeId":"e2e","question":"vpn khong ket noi","category":"IT"}'
Invoke-RestMethod 'http://localhost:5003/debug/saga-instances?ticketId=TCK-001'
Invoke-RestMethod http://localhost:5001/debug/outbox
```

### SQL Server local (saga Consumer Outbox enabled)

Starts Service Bus emulator + SQL Server on `localhost:1433` (`sa` / `SupportPoc_LocalSql1!`).

```powershell
docker compose -f .emulator/docker-compose.yml up -d
$csTickets = 'Server=localhost,1433;Database=supportpoc_tickets;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;'
$csOrch = 'Server=localhost,1433;Database=supportpoc_orchestrator;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;'
$csKnowledge = 'Server=localhost,1433;Database=supportpoc_knowledge;User Id=sa;Password=SupportPoc_LocalSql1!;TrustServerCertificate=True;'
$env:ASPNETCORE_ENVIRONMENT='Development'
$env:ServiceBus__ConnectionString='Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;'
$env:AzureAd__Enabled='false'
$env:AzureOpenAI__ChatEnabled='false'
$env:ConnectionStrings__Tickets=$csTickets
$env:ConnectionStrings__Orchestrator=$csOrch
$env:ConnectionStrings__Knowledge=$csKnowledge
$env:AutoSuggestion__UseSagaConsumerOutbox='true'
dotnet run --project src/TicketService/SupportPoc.TicketService.csproj --no-launch-profile --urls http://localhost:5001
dotnet run --project src/AiOrchestrator/SupportPoc.AiOrchestrator.csproj --no-launch-profile --urls http://localhost:5003
```

Verify saga outbox: `Invoke-RestMethod http://localhost:5003/ready` → `messaging.sagaConsumerOutbox` should be `true`.

If a reusable workflow is needed, prefer documenting the direct command sequence here instead of adding a new script.
