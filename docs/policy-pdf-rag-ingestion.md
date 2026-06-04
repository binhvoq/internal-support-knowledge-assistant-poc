# Policy PDF RAG Ingestion

## Requirement

Admin can upload internal policy PDFs into the knowledge base. The AI assistant must later use those documents for RAG when users ask questions in ticket auto-suggestion or chat. The admin UI should show whether each uploaded file has been read, digested, indexed, and is ready for advisory answers.

## Current Implementation

- `KnowledgeService` exposes `POST /documents/upload-pdf`.
- The endpoint accepts multipart form data:
  - `file`: PDF file, max 20 MB for this POC.
  - `title`: optional display title.
  - `category`: optional support category, defaults to `Other`.
- The service extracts embedded text from the PDF.
- Extracted text is stored as a `KnowledgeDocumentEntity`.
- If Azure Blob Storage is configured, the extracted text is uploaded as a `.txt` knowledge source.
- If Azure AI Search is configured, the document is embedded and upserted into the search index. The service then polls Azure AI Search by `documentId` and only marks the file `Ready` after the index can query that document.
- If Azure AI Search is not configured, the document is still marked `Ready` and will be used by local keyword fallback.
- The `Knowledge Admin` UI now shows:
  - total documents,
  - ready documents,
  - failed ingestion count,
  - re-index status,
  - per-document ingestion status and message,
  - a delete action that removes the document from Azure AI Search, Azure Blob Storage, and the local knowledge DB.
- `KnowledgeService` exposes `DELETE /documents/{id}` for cleanup. Delete removes the Azure AI Search index entry first, then the blob, then the local DB row.

## Demo PDF Files

Local demo PDFs live under `test-pdfs/`. The directory is intentionally ignored by git so generated customer-demo files stay on the machine but are not committed.

Current local demo files:

- `test-pdfs/device-replacement-policy.pdf`
- `test-pdfs/expense-reimbursement-policy.pdf`
- `test-pdfs/parental-leave-policy.pdf`
- `test-pdfs/remote-work-policy.pdf`

`device-replacement-policy.pdf` is a demo seed file. On `KnowledgeService` startup, if the file exists and no document with that filename/title is present, the service automatically ingests it as:

- title: `Device Replacement Policy Demo Seed`
- category: `IT`
- marker: `RAG-DEVICE-4YEARS`

Keep the other PDFs in place for customer demos of the "digest PDF" flow:

1. Open `Knowledge Admin`.
2. Upload one of the remaining PDFs.
3. Wait until the file appears as `San sang tu van` / `Ready`.
4. Ask `Support Copilot` about the marker in that PDF.

Useful demo prompts:

- `Policy co support code RAG-EXPENSE-30DAYS noi gi ve reimbursement?`
- `Policy co support code RAG-PARENTAL-16WEEKS noi gi ve parental leave?`
- `Policy co support code RAG-AZURE-REAL-TEST noi gi ve work from home?`

## Status Model

- `Processing`: upload was accepted and extraction/indexing is in progress.
- `Ready`: AI has extracted text and the document is queryable through the configured search path. For Azure AI Search this means the document was upserted and then observed through a search query, not merely submitted.
- `Failed`: extraction or indexing failed; the message explains the error.

Seed/manual documents default to `Ready`.

## RAG Flow

1. Admin uploads a policy PDF from `Knowledge Admin`.
2. `KnowledgeService` extracts text and stores the document.
3. `KnowledgeService` indexes the document in Azure AI Search when configured.
4. `AiOrchestrator` already calls `KnowledgeSearchClient` for related documents.
5. Ticket suggestions and chat responses can cite/use the newly uploaded policy once the document is `Ready`.

`/ai/chat` does a direct pre-search against `KnowledgeService` before calling Azure OpenAI and includes those related documents in the system prompt. MCP `search_knowledge` remains available, but chat does not rely only on the model deciding to call the tool.

## Delete Flow

Use the `Xoa` button in `Knowledge Admin` or call `DELETE /documents/{id}`.

The delete endpoint is intentionally ordered:

1. Delete the Azure AI Search document by `documentId`.
2. Delete the extracted text blob named `{documentId}.txt`.
3. Delete the local DB row.

If Azure AI Search deletion fails, the local document is kept so the UI does not claim the policy is gone while it can still be retrieved.

## Azure Resources

Already covered by existing repo configuration:

- Azure AI Search: vector/hybrid retrieval.
- Azure OpenAI embeddings: creates vectors for uploaded policy content.
- Azure Storage Blob: optional source storage for extracted text.

Potential additions if the product scope expands:

- Azure AI Document Intelligence: needed for scanned/image-only PDFs or OCR-quality extraction.
- Azure Service Bus queue/topic for async ingestion: useful when PDFs become large or indexing should not block the upload request.
- Azure Blob container for original PDFs: useful if the system must retain the exact uploaded source file, not only extracted text.

## Limitations

- Current parser supports PDFs with embedded selectable text.
- Scanned PDFs return a validation error and need OCR before RAG.
- The POC indexes a whole PDF as one document. For production, split long documents into chunks with page metadata before embedding.
- No retry endpoint is implemented yet; failed documents can be diagnosed from the ingestion message and then deleted/re-uploaded.
