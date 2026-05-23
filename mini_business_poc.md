# Mini Technical PoC: Internal Support Knowledge Assistant

## 1. Muc tieu

Xay dung mot PoC nho cho he thong tro ly ho tro noi bo, giup nhan vien tao ticket, tim cau tra loi tu kho tai lieu noi bo, va goi y buoc xu ly tiep theo bang AI.

Trong PoC nay, nghiep vu duoc giu gon: chi tap trung vao ticket ho tro noi bo cho IT/HR/Finance. Muc tieu chinh la co du khong gian de thuc hanh cac ky thuat trong `technical_learn.md`, khong phai xay mot san pham day du.

## 2. Boi canh nghiep vu

Cong ty co nhieu tai lieu noi bo nhu:

- Huong dan reset mat khau.
- Chinh sach nghi phep.
- Quy trinh reimburse chi phi.
- Huong dan cap quyen phan mem.
- FAQ ve thiet bi lam viec.

Nhan vien thuong gui cau hoi trung lap cho team support. Team support muon co mot cong cu mini:

- Nhan vien nhap cau hoi va tao ticket.
- He thong tim tai lieu lien quan.
- AI tom tat cau tra loi de xuat.
- Neu cau hoi ro rang, AI co the goi function de tao ticket, cap nhat trang thai, hoac lay thong tin ticket.
- Support agent xem danh sach ticket, cau tra loi goi y, va chap nhan/chinh sua truoc khi gui.

## 3. Pham vi mini

Chi can lam 3 luong chinh:

1. Nhan vien tao cau hoi ho tro.
2. He thong tim tai lieu lien quan va sinh cau tra loi goi y.
3. Support agent quan ly ticket va ghi nhan ket qua xu ly.

Khong can lam day du authentication, phan quyen phuc tap, billing, notification that, hay dashboard nang cao.

## 4. Actor

### Employee

Nguoi dung noi bo tao cau hoi ho tro.

### Support Agent

Nguoi xu ly ticket, xem AI suggestion, cap nhat trang thai ticket.

### AI Assistant

Thanh phan dung LLM tren Azure OpenAI de doc cau hoi, tim tri thuc lien quan, goi function khi can, va de xuat cau tra loi.

## 5. Core business flow

### Flow 1: Tao cau hoi ho tro

1. Employee mo ung dung React.
2. Employee nhap noi dung cau hoi, vi du: "Toi quen mat khau VPN, can lam gi?"
3. Frontend goi Ticket Service de tao ticket moi voi trang thai `New`.
4. Ticket Service publish event `TicketCreated`.
5. AI Orchestrator Service nhan event nay.
6. AI Orchestrator dung Azure AI Search va Vector search de tim tai lieu lien quan.
7. AI Orchestrator dung Semantic Kernel ket hop Azure OpenAI de tao cau tra loi goi y.
8. AI Orchestrator cap nhat ticket voi truong `aiSuggestedAnswer`.
9. Support Agent thay ticket va cau tra loi goi y tren man hinh.

### Flow 2: Support agent xu ly ticket

1. Support Agent mo danh sach ticket.
2. Support Agent chon ticket.
3. He thong hien:
   - Noi dung cau hoi.
   - Tai lieu lien quan duoc tim thay.
   - Cau tra loi AI goi y.
   - Trang thai hien tai.
4. Support Agent co the sua cau tra loi.
5. Support Agent bam `Resolve`.
6. Ticket Service cap nhat trang thai thanh `Resolved`.
7. Ticket Service publish event `TicketResolved`.

### Flow 3: AI function calling

AI Assistant co the dung Function Calling cho cac tac vu nho:

- `create_ticket(employeeId, category, question)`
- `get_ticket_status(ticketId)`
- `update_ticket_status(ticketId, status)`
- `search_policy_documents(query)`

Vi du employee hoi: "Ticket hom qua cua toi xu ly den dau roi?" AI se goi `get_ticket_status` thay vi chi tra loi bang text.

## 6. Kien truc de xuat

PoC co the chia thanh cac service nho theo huong Microservices:

### Frontend React App

- Man hinh tao cau hoi.
- Man hinh danh sach ticket.
- Man hinh chi tiet ticket.
- Man hinh quan ly tai lieu noi bo don gian.

### Ticket Service

- Quan ly ticket.
- API tao ticket, lay ticket, cap nhat ticket.
- Publish event `TicketCreated`, `TicketUpdated`, `TicketResolved`.

### Knowledge Service

- Quan ly danh sach tai lieu noi bo.
- Upload hoac seed tai lieu markdown/text.
- Dong bo tai lieu sang Azure AI Search.
- Tao embedding de dung Vector search.

### AI Orchestrator Service

- Lang nghe event tu Ticket Service.
- Dung Semantic Kernel de dieu phoi prompt, memory, plugin/function.
- Goi Azure OpenAI LLM de sinh cau tra loi.
- Dung Azure AI Search de truy van tai lieu bang keyword search va Vector search.
- Dung Function Calling de goi cac function nghiep vu khi can.

### MCP Tool Server

- Cung cap tool noi bo theo chuan MCP (Model Context Protocol).
- Tool co the bao gom:
  - Lay ticket theo ID.
  - Tim tai lieu noi bo.
  - Lay danh sach category ho tro.
  - Cap nhat trang thai ticket.

MCP giup tach cac cong cu nghiep vu ra khoi logic prompt/orchestrator, de AI Assistant co the goi tool theo interface ro rang.

## 7. Event-Driven Architecture

Dung Event-Driven Architecture de cac service khong phu thuoc truc tiep qua nhau qua cac luong bat dong bo.

Event toi thieu:

- `TicketCreated`
- `TicketUpdated`
- `TicketResolved`
- `KnowledgeDocumentUploaded`
- `KnowledgeIndexUpdated`
- `AISuggestionGenerated`

Vi du:

1. Ticket Service tao ticket va publish `TicketCreated`.
2. AI Orchestrator nhan event, tao suggestion.
3. AI Orchestrator publish `AISuggestionGenerated`.
4. Ticket Service hoac frontend cap nhat hien thi suggestion.

Tren Azure, co the dung Azure Service Bus hoac Azure Event Grid cho phan event.

## 8. Azure AI Search va Vector search

Azure AI Search duoc dung lam knowledge retrieval layer.

Du lieu index gom:

- `documentId`
- `title`
- `category`
- `content`
- `embedding`
- `sourceUrl`
- `updatedAt`

Can ho tro:

- Keyword search: tim theo tu khoa truc tiep.
- Vector search: tim theo y nghia cau hoi.
- Hybrid search: ket hop keyword va vector.

Vi du cau hoi:

> "May tinh cua toi khong vao duoc VPN"

He thong co the tim duoc tai lieu "Huong dan reset VPN profile" du cau hoi khong trung tu khoa hoan toan.

## 9. Semantic Kernel

Semantic Kernel duoc dung trong AI Orchestrator Service de:

- Quan ly prompt template.
- Dang ky plugin/function nghiep vu.
- Dieu phoi luong RAG: nhan cau hoi, search tai lieu, tao cau tra loi.
- Goi Function Calling khi LLM can thuc hien hanh dong.

Plugin goi y:

- `TicketPlugin`
- `KnowledgeSearchPlugin`
- `PolicyDocumentPlugin`

## 10. LLM va Azure OpenAI

Dung LLM thong qua Azure OpenAI cho cac tac vu:

- Phan loai category cua ticket: IT, HR, Finance, Other.
- Tao cau tra loi goi y dua tren tai lieu noi bo.
- Tom tat ticket cho support agent.
- Rut trich thong tin can thiet tu cau hoi.
- Quyet dinh khi nao can goi function.

Prompt can yeu cau AI:

- Chi tra loi dua tren tai lieu tim duoc.
- Neu khong du thong tin, noi ro can support agent xu ly.
- Khong tu bia chinh sach noi bo.
- Tra ve cau tra loi ngan gon, de agent co the sua nhanh.

## 11. React UI

React app nen co cac man hinh nho:

### Employee View

- Form nhap cau hoi.
- Dropdown category tuy chon.
- Nut tao ticket.
- Vung hien ticket ID va trang thai.

### Support Queue View

- Bang ticket.
- Loc theo status/category.
- Badge hien ticket nao da co AI suggestion.

### Ticket Detail View

- Noi dung cau hoi.
- AI suggested answer.
- Danh sach tai lieu lien quan.
- Nut `Resolve`, `Reopen`, `Update`.

### Knowledge Admin View

- Form them tai lieu noi bo.
- Danh sach tai lieu.
- Nut re-index.

## 12. Terraform va Azure

Dung Terraform de khai bao infrastructure tren Azure.

Tai nguyen toi thieu:

- Azure Resource Group.
- Azure App Service hoac Azure Container Apps cho cac service.
- Azure Service Bus hoac Event Grid cho event.
- Azure AI Search.
- Azure OpenAI.
- Azure Storage Account cho tai lieu raw.
- Azure Cosmos DB hoac Azure SQL cho ticket/document metadata.
- Application Insights de log va trace.

PoC co the chay local truoc, nhung Terraform van nen co folder rieng de mo ta cloud target.

## 13. GitHub workflow

Dung GitHub de quan ly code va CI/CD co ban.

Goi y repository structure:

```text
/frontend
/services/ticket-service
/services/knowledge-service
/services/ai-orchestrator
/services/mcp-tool-server
/infra/terraform
/docs
```

GitHub Actions toi thieu:

- Build frontend.
- Build backend services.
- Run unit tests.
- Run lint.
- Terraform validate/plan cho folder `/infra/terraform`.

## 14. Data model toi thieu

### Ticket

```json
{
  "id": "TCK-001",
  "employeeId": "EMP-001",
  "category": "IT",
  "question": "Toi quen mat khau VPN, can lam gi?",
  "status": "New",
  "aiSuggestedAnswer": "Ban co the thu reset mat khau VPN theo huong dan...",
  "relatedDocumentIds": ["DOC-001"],
  "createdAt": "2026-05-23T09:00:00Z",
  "updatedAt": "2026-05-23T09:03:00Z"
}
```

### KnowledgeDocument

```json
{
  "id": "DOC-001",
  "title": "Huong dan reset mat khau VPN",
  "category": "IT",
  "content": "Cac buoc reset mat khau VPN...",
  "sourceUrl": "internal://it/vpn-reset",
  "updatedAt": "2026-05-23T08:00:00Z"
}
```

## 15. API toi thieu

### Ticket Service

- `POST /tickets`
- `GET /tickets`
- `GET /tickets/{id}`
- `PATCH /tickets/{id}`
- `POST /tickets/{id}/resolve`

### Knowledge Service

- `POST /documents`
- `GET /documents`
- `POST /documents/reindex`
- `GET /search?query=...`

### AI Orchestrator Service

- `POST /ai/suggest-answer`
- `POST /ai/chat`
- `POST /ai/classify-ticket`

### MCP Tool Server

- Tool: `get_ticket`
- Tool: `update_ticket_status`
- Tool: `search_knowledge`
- Tool: `list_support_categories`

## 16. Acceptance criteria

PoC duoc xem la dat khi:

- React app tao duoc ticket moi.
- Ticket Service publish duoc event `TicketCreated`.
- AI Orchestrator nhan event va sinh duoc `aiSuggestedAnswer`.
- Azure AI Search tra ve duoc tai lieu lien quan bang Vector search.
- Semantic Kernel duoc dung de dieu phoi prompt va plugin/function.
- Co it nhat mot demo Function Calling, vi du lay trang thai ticket.
- MCP Tool Server expose duoc it nhat hai tool nghiep vu.
- Terraform co the validate va mo ta duoc cac Azure resources chinh.
- GitHub Actions build/test/validate duoc project.

## 17. Ly do de tai phu hop de luyen tap

De tai nay nho ve nghiep vu nhung cham du cac keyword can hoc:

- Microservices: chia Ticket, Knowledge, AI Orchestrator, MCP Tool Server.
- Event-Driven Architecture: ticket va AI suggestion trao doi qua event.
- Azure AI Search: lam search engine cho knowledge base.
- Semantic Kernel: dieu phoi AI workflow va plugin.
- Function Calling: AI goi function de doc/cap nhat ticket.
- MCP (Model Context Protocol): expose tool nghiep vu cho AI.
- LLM: sinh cau tra loi, phan loai, tom tat.
- Azure OpenAI: provider LLM chinh.
- Vector search: tim tai lieu theo ngu nghia.
- React: frontend cho employee va support agent.
- Terraform: khai bao ha tang.
- Azure: moi truong cloud target.
- GitHub: source control va CI/CD.

