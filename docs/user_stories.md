# User Stories: Internal Support Knowledge Assistant

## 1. Muc dich tai lieu

Tai lieu nay chuyen y tuong trong `mini_business_poc.md` thanh cac user story co the dung de lap ke hoach va giao viec cho AI/coder khi implement PoC.

Pham vi van la mini PoC. Khong yeu cau san pham hoan chinh, nhung moi story can co dau vao, dau ra, acceptance criteria va goi y ky thuat de dam bao du cac cong nghe can luyen.

Kien truc tong the di theo huong Microservices, trong do Ticket Service, Knowledge Service, AI Orchestrator va MCP Tool Server co ranh gioi rieng.

## 2. Actors

### Employee

Nhan vien noi bo can hoi ve IT, HR hoac Finance.

### Support Agent

Nguoi tiep nhan ticket, xem AI suggestion, chinh sua cau tra loi va resolve ticket.

### Knowledge Admin

Nguoi them tai lieu noi bo vao knowledge base va yeu cau re-index.

### AI Assistant

Thanh phan dung LLM, Azure OpenAI, Semantic Kernel, Function Calling, MCP tools va Azure AI Search de ho tro luong nghiep vu.

## 3. Epic 1: Employee tao ticket ho tro

### Story 1.1: Tao ticket tu cau hoi cua employee

As an Employee, I want to submit a support question, so that the support team can help me resolve my issue.

#### UI

- Man hinh React co form tao ticket.
- Field bat buoc:
  - Employee ID.
  - Question.
- Field tuy chon:
  - Category: IT, HR, Finance, Other.

#### API

- `POST /tickets`

Request:

```json
{
  "employeeId": "EMP-001",
  "category": "IT",
  "question": "Toi quen mat khau VPN, can lam gi?"
}
```

Response:

```json
{
  "id": "TCK-001",
  "employeeId": "EMP-001",
  "category": "IT",
  "question": "Toi quen mat khau VPN, can lam gi?",
  "status": "New",
  "createdAt": "2026-05-23T09:00:00Z"
}
```

#### Acceptance criteria

- Employee tao duoc ticket moi tu UI.
- Neu question trong, UI hien validation error.
- Ticket moi co status mac dinh la `New`.
- Ticket Service luu ticket vao database.
- Ticket Service publish event `TicketCreated`.

#### Technical notes

- React dung form state va call API.
- Ticket Service la mot microservice rieng.
- Event-Driven Architecture: publish `TicketCreated` vao Azure Service Bus hoac Event Grid.
- GitHub Actions can build/test Ticket Service.

## 4. Epic 2: Support Agent quan ly ticket

### Story 2.1: Xem danh sach ticket

As a Support Agent, I want to view all support tickets, so that I can decide what to handle next.

#### UI

- Man hinh React hien bang ticket.
- Cot toi thieu:
  - Ticket ID.
  - Employee ID.
  - Category.
  - Status.
  - Short question.
  - AI suggestion status.
  - Created at.
- Filter:
  - Status.
  - Category.

#### API

- `GET /tickets`

#### Acceptance criteria

- Support Agent xem duoc danh sach ticket.
- Co the filter theo status.
- Co the filter theo category.
- Ticket nao da co AI suggestion thi co badge `AI Ready`.

#### Technical notes

- React table/list component.
- Ticket Service expose query API.
- Status values: `New`, `Analyzing`, `Suggested`, `Resolved`, `Reopened`.

### Story 2.2: Xem chi tiet ticket

As a Support Agent, I want to view ticket details with AI suggestion and related documents, so that I can respond faster.

#### UI

- Man hinh chi tiet ticket hien:
  - Question goc.
  - Category.
  - Status.
  - AI suggested answer.
  - Related documents.
  - Action buttons: `Resolve`, `Reopen`, `Save Answer`.

#### API

- `GET /tickets/{id}`

#### Acceptance criteria

- Support Agent mo duoc chi tiet ticket.
- Neu chua co AI suggestion, UI hien trang thai `Analyzing` hoac `No suggestion yet`.
- Neu co related documents, UI hien title va score cua tung document.
- UI khong crash khi AI suggestion rong.

#### Technical notes

- Ticket aggregate co the luu `aiSuggestedAnswer` va `relatedDocuments`.
- Related documents den tu Azure AI Search va Vector search.

### Story 2.3: Resolve ticket

As a Support Agent, I want to resolve a ticket with a final answer, so that the issue is marked as completed.

#### UI

- Agent co the sua final answer.
- Agent bam `Resolve`.

#### API

- `POST /tickets/{id}/resolve`

Request:

```json
{
  "finalAnswer": "Ban hay vao portal VPN va bam Forgot Password de reset mat khau."
}
```

#### Acceptance criteria

- Ticket status chuyen thanh `Resolved`.
- Final answer duoc luu vao ticket.
- Ticket Service publish event `TicketResolved`.
- Ticket da resolve van xem lai duoc trong danh sach.

#### Technical notes

- Dung event `TicketResolved` de minh hoa Event-Driven Architecture.
- Co unit test cho transition status.

## 5. Epic 3: Knowledge Admin quan ly tai lieu noi bo

### Story 3.1: Them tai lieu noi bo

As a Knowledge Admin, I want to add internal documents, so that AI can use them when answering support questions.

#### UI

- Form them document:
  - Title.
  - Category.
  - Content.
  - Source URL optional.

#### API

- `POST /documents`

Request:

```json
{
  "title": "Huong dan reset mat khau VPN",
  "category": "IT",
  "content": "Buoc 1: Mo portal VPN. Buoc 2: Chon Forgot Password...",
  "sourceUrl": "internal://it/vpn-reset"
}
```

#### Acceptance criteria

- Admin them duoc document moi.
- Document duoc luu vao database hoac storage.
- Knowledge Service publish event `KnowledgeDocumentUploaded`.
- Document co the duoc dua vao Azure AI Search index.

#### Technical notes

- Knowledge Service la microservice rieng.
- Co the luu raw document vao Azure Storage Account.
- Metadata luu o Cosmos DB, Azure SQL, hoac database local cho PoC.

### Story 3.2: Re-index knowledge base

As a Knowledge Admin, I want to re-index documents, so that the search index is up to date.

#### UI

- Nut `Re-index`.
- Trang thai:
  - Idle.
  - Indexing.
  - Completed.
  - Failed.

#### API

- `POST /documents/reindex`

#### Acceptance criteria

- Admin trigger duoc re-index.
- Knowledge Service tao embedding cho documents.
- Knowledge Service cap nhat Azure AI Search index.
- Sau khi re-index, search theo keyword va Vector search tra ve document lien quan.
- Knowledge Service publish event `KnowledgeIndexUpdated`.

#### Technical notes

- Dung Azure OpenAI embedding model de tao vector.
- Dung Azure AI Search index co field `embedding`.
- Nen co seed data de demo nhanh.

## 6. Epic 4: AI sinh cau tra loi goi y

### Story 4.1: Tu dong sinh AI suggestion khi ticket moi duoc tao

As a Support Agent, I want the system to automatically generate an AI suggested answer for new tickets, so that I can respond faster.

#### Trigger

- Event `TicketCreated`.

#### Processing

1. AI Orchestrator nhan event.
2. AI Orchestrator lay ticket detail.
3. AI Orchestrator goi Azure AI Search:
   - Keyword search.
   - Vector search.
   - Hybrid search neu co.
4. AI Orchestrator dung Semantic Kernel de tao prompt.
5. AI Orchestrator goi Azure OpenAI LLM.
6. AI Orchestrator luu `aiSuggestedAnswer` va `relatedDocumentIds` vao ticket.
7. AI Orchestrator publish event `AISuggestionGenerated`.

#### Acceptance criteria

- Khi tao ticket moi, he thong tu dong bat dau analyze.
- Ticket status co the chuyen tu `New` sang `Analyzing`.
- Sau khi AI xu ly xong, ticket co status `Suggested`.
- Cau tra loi AI co dua tren document tim duoc.
- Neu khong tim thay document phu hop, AI phai noi ro khong du thong tin.

#### Technical notes

- Day la luong chinh de dung LLM, Azure OpenAI, Azure AI Search, Vector search va Semantic Kernel.
- Can log prompt input/output o muc an toan cho debug PoC.

### Story 4.2: Phan loai ticket bang LLM

As a Support Agent, I want the system to classify ticket category automatically, so that tickets are easier to route.

#### Trigger

- Khi Employee khong chon category hoac chon `Other`.

#### Output

```json
{
  "category": "IT",
  "confidence": 0.87,
  "reason": "Question mentions VPN password reset."
}
```

#### Acceptance criteria

- LLM phan loai ticket vao IT, HR, Finance hoac Other.
- Neu confidence thap, category giu la `Other`.
- Ket qua classification duoc luu vao ticket.

#### Technical notes

- Dung Azure OpenAI.
- Co the implement trong Semantic Kernel plugin hoac AI Orchestrator service method.

## 7. Epic 5: Function Calling

### Story 5.1: AI lay trang thai ticket bang function

As an Employee, I want to ask the assistant about my ticket status, so that I do not need to search manually.

#### Example user message

```text
Ticket TCK-001 cua toi xu ly den dau roi?
```

#### Function

```text
get_ticket_status(ticketId)
```

#### Expected function result

```json
{
  "ticketId": "TCK-001",
  "status": "Suggested",
  "lastUpdatedAt": "2026-05-23T09:05:00Z"
}
```

#### Acceptance criteria

- AI nhan ra user dang hoi status ticket.
- AI goi function `get_ticket_status`.
- AI tra loi dua tren ket qua function.
- AI khong tu bia status neu function loi.

#### Technical notes

- Dung Function Calling cua Azure OpenAI.
- Function co the map toi Ticket Service API.
- Co the dang ky function qua Semantic Kernel.

### Story 5.2: AI cap nhat status ticket bang function

As a Support Agent, I want the assistant to update ticket status from a command, so that I can work faster.

#### Example agent message

```text
Resolve ticket TCK-001 voi cau tra loi: Da huong dan employee reset VPN password.
```

#### Function

```text
update_ticket_status(ticketId, status, finalAnswer)
```

#### Acceptance criteria

- AI extract duoc ticket ID.
- AI extract duoc status mong muon.
- AI goi function update.
- Ticket Service cap nhat status.
- Neu thieu final answer khi resolve, AI hoi lai thay vi update.

#### Technical notes

- Dung Function Calling.
- Can validate status truoc khi update.

## 8. Epic 6: MCP Tool Server

### Story 6.1: Expose tool lay ticket qua MCP

As an AI Assistant, I want to call a MCP tool to get ticket details, so that business tools are decoupled from the AI runtime.

#### MCP tool

```text
get_ticket
```

Input:

```json
{
  "ticketId": "TCK-001"
}
```

Output:

```json
{
  "id": "TCK-001",
  "status": "Suggested",
  "question": "Toi quen mat khau VPN, can lam gi?",
  "aiSuggestedAnswer": "..."
}
```

#### Acceptance criteria

- MCP Tool Server expose tool `get_ticket`.
- AI Orchestrator goi duoc tool nay.
- Tool tra ve loi ro rang neu ticket khong ton tai.

#### Technical notes

- MCP server co the la mot service rieng.
- Tool implementation goi Ticket Service.

### Story 6.2: Expose tool search knowledge qua MCP

As an AI Assistant, I want to call a MCP tool to search internal knowledge, so that retrieval logic is reusable.

#### MCP tool

```text
search_knowledge
```

Input:

```json
{
  "query": "quen mat khau VPN",
  "category": "IT"
}
```

Output:

```json
{
  "results": [
    {
      "documentId": "DOC-001",
      "title": "Huong dan reset mat khau VPN",
      "score": 0.92
    }
  ]
}
```

#### Acceptance criteria

- MCP Tool Server expose tool `search_knowledge`.
- Tool goi Knowledge Service hoac Azure AI Search.
- Ket qua gom title, documentId va score.

#### Technical notes

- Tool nay giup luyen MCP (Model Context Protocol) va Azure AI Search.

## 9. Epic 7: Infrastructure va DevOps

### Story 7.1: Khai bao Azure infrastructure bang Terraform

As a Developer, I want infrastructure to be described with Terraform, so that the PoC can be deployed consistently.

#### Resources

- Azure Resource Group.
- Azure AI Search.
- Azure OpenAI.
- Azure Service Bus hoac Event Grid.
- Azure Storage Account.
- Azure Container Apps hoac App Service.
- Database cho ticket/document metadata.
- Application Insights.

#### Acceptance criteria

- Folder `/infra/terraform` ton tai.
- `terraform fmt` pass.
- `terraform validate` pass.
- Variables tach rieng cho environment name, location va resource prefix.

#### Technical notes

- PoC co the de Terraform o muc scaffold, chua can apply that neu chua co Azure subscription.

### Story 7.2: GitHub Actions CI

As a Developer, I want GitHub Actions to validate the project, so that broken changes are detected early.

#### Workflow

- Build React frontend.
- Build backend services.
- Run unit tests.
- Run lint neu co.
- Run `terraform fmt -check`.
- Run `terraform validate`.

#### Acceptance criteria

- Co workflow file trong `.github/workflows`.
- Push/PR trigger duoc CI.
- CI fail neu build hoac test loi.

#### Technical notes

- Co the tach thanh `frontend-ci.yml`, `backend-ci.yml`, `infra-ci.yml` neu can.

## 10. Definition of Done cho PoC

PoC duoc xem la hoan thanh khi co the demo end-to-end:

1. Admin them hoac seed tai lieu noi bo.
2. Knowledge Service index tai lieu vao Azure AI Search voi Vector search.
3. Employee tao ticket tu React UI.
4. Ticket Service publish event `TicketCreated`.
5. AI Orchestrator nhan event va dung Semantic Kernel + Azure OpenAI de tao suggestion.
6. AI Orchestrator dung Azure AI Search de lay related documents.
7. Support Agent xem ticket, AI suggestion va related documents.
8. Support Agent resolve ticket.
9. AI Assistant goi duoc it nhat mot Function Calling.
10. MCP Tool Server expose duoc it nhat `get_ticket` va `search_knowledge`.
11. Terraform validate pass.
12. GitHub Actions CI pass.

## 11. Thu tu implement goi y cho AI/coder

1. Tao repository structure.
2. Implement Ticket Service CRUD don gian.
3. Implement React UI tao ticket va xem danh sach ticket.
4. Them event publish cho `TicketCreated`.
5. Implement Knowledge Service voi seed documents.
6. Tao Azure AI Search index va Vector search flow.
7. Implement AI Orchestrator voi Semantic Kernel va Azure OpenAI.
8. Them Function Calling cho `get_ticket_status`.
9. Implement MCP Tool Server voi `get_ticket` va `search_knowledge`.
10. Them Terraform scaffold.
11. Them GitHub Actions CI.
