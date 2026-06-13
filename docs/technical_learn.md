Microservices
Event-Driven Architecture
Azure AI Search
Semantic Kernel
Function Calling
MCP (Model Context Protocol)
LLM
Azure OpenAI
Vector search
React
Terraform
Azure
GitHub

Saga Pattern Orchestration
Idempotency
Inbox Pattern
Outbox Pattern
Entra ID

## Design Note: Auto Suggestion And Agent Chat Are Separate By Design

The concepts above are demonstrated across two AI flows on purpose.

The auto-suggestion flow is the main event-driven business workflow:

`TicketService` creates ticket -> `ITicketCreated` event -> `AiOrchestrator`
saga -> durable AI generation attempt -> background worker -> fixed AI pipeline
-> propose suggestion back to `TicketService`.

This flow intentionally uses a deterministic pipeline:

`classify -> search knowledge -> generate suggestion`

The goal is to show why Saga Pattern Orchestration is useful: the workflow has
multiple steps, state transitions, retries, timeouts, idempotency, stale worker
lease handling, late message handling, and reconcile logic. If the saga only
wrapped one direct "let the LLM call tools" operation, it would demonstrate much
less of the reliability problem that sagas are meant to solve.

The interactive chat flow is separate:

`POST /ai/chat` -> `TicketSuggestionService` -> Semantic Kernel -> allowed MCP
functions -> Azure OpenAI function calling -> MCP tool server.

This flow demonstrates the agent/tooling concepts: Semantic Kernel, Function
Calling, MCP tool discovery, MCP tool policies, and role-aware tool access.

So the fact that `Semantic Kernel + Function Calling + MCP` are not used by the
auto-suggestion saga path is intentional, not an implementation gap. The POC
keeps the background saga deterministic to highlight event-driven reliability
patterns, while the chat endpoint highlights agent-style tool calling.