# Scripts

This project intentionally does not keep shell automation scripts here.

AI agents should run the required commands directly and report the exact commands/results instead of creating one-off `.sh` files.

Common direct commands:

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

If a reusable workflow is needed, prefer documenting the direct command sequence here instead of adding a new script.
