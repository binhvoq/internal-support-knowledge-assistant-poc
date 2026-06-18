import { useEffect, useState } from 'react';
import { api, type KnowledgeDocument } from '../api';
import { AuthRequiredBanner } from '../auth/AuthRequiredBanner';
import { useAuth } from '../auth/AuthContext';
import { categories } from '../constants';
import { EmptyState } from '../components/EmptyState';
import { Icon } from '../components/Icon';
import { Kpi } from '../components/Kpi';
import { SectionCard } from '../components/SectionCard';
import { formatDate } from '../utils/format';

export function KnowledgeView() {
  const auth = useAuth();
  const [docs, setDocs] = useState<KnowledgeDocument[]>([]);
  const [pdfFile, setPdfFile] = useState<File | null>(null);
  const [pdfTitle, setPdfTitle] = useState('');
  const [title, setTitle] = useState('');
  const [category, setCategory] = useState('IT');
  const [content, setContent] = useState('');
  const [sourceUrl, setSourceUrl] = useState('');
  const [reindexStatus, setReindexStatus] = useState('Idle');
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const [uploading, setUploading] = useState(false);

  const readyCount = docs.filter((document) => document.ingestionStatus === 'Ready').length;
  const failedCount = docs.filter((document) => document.ingestionStatus === 'Failed').length;

  const load = async () => {
    setDocs(await api.listDocuments());
    const status = await api.reindexStatus();
    setReindexStatus(status.status);
  };

  useEffect(() => {
    if (auth.configured && (!auth.ready || !auth.account)) {
      setDocs([]);
      setReindexStatus('Idle');
      setError('');
      return;
    }

    void Promise.all([api.listDocuments(), api.reindexStatus()])
      .then(([documents, status]) => {
        setDocs(documents);
        setReindexStatus(status.status);
        setError('');
      })
      .catch((e) => setError((e as Error).message));
  }, [auth.configured, auth.ready, auth.account]);

  const addDoc = async () => {
    setError('');
    setMessage('');
    if (!title.trim()) {
      setError('Tiêu đề không được để trống.');
      return;
    }
    if (!content.trim()) {
      setError('Nội dung không được để trống.');
      return;
    }
    try {
      await api.createDocument({
        title: title.trim(),
        category,
        content: content.trim(),
        sourceUrl: sourceUrl.trim() || undefined,
      });
      setTitle('');
      setContent('');
      setSourceUrl('');
      setMessage('Đã thêm tài liệu.');
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const uploadPdf = async () => {
    setError('');
    setMessage('');
    if (!pdfFile) {
      setError('Chọn một file PDF trước khi upload.');
      return;
    }
    try {
      setUploading(true);
      const document = await api.uploadPdfDocument({
        file: pdfFile,
        title: pdfTitle.trim() || undefined,
        category,
      });
      setPdfFile(null);
      setPdfTitle('');
      setMessage(
        document.ingestionStatus === 'Ready'
          ? `${document.title} đã được AI đọc xong và sẵn sàng tư vấn.`
          : `${document.title} đã upload, ingestion status là ${document.ingestionStatus}.`,
      );
      await load();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setUploading(false);
    }
  };

  const reindex = async () => {
    setError('');
    setMessage('Đang re-index...');
    try {
      const result = await api.reindex();
      setMessage(`Re-index: ${result.status}, ${result.documentCount} documents.`);
      await load();
    } catch (e) {
      setMessage('');
      setError((e as Error).message);
    }
  };

  const deleteDoc = async (document: KnowledgeDocument) => {
    setError('');
    setMessage('');
    const confirmed = window.confirm(`Xóa ${document.title} khỏi DB, blob và Azure AI Search?`);
    if (!confirmed) return;
    try {
      await api.deleteDocument(document.id);
      setMessage(`Đã xóa ${document.title}.`);
      await load();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <>
      <h1>Knowledge Admin</h1>
      <AuthRequiredBanner action="quản lý tài liệu (cần role Support.KnowledgeAdmin)" />
      <div className="kpi-grid">
        <Kpi icon="file" value={readyCount} label="Ready" />
        <Kpi icon="file" value={docs.length} label="Total docs" />
        <Kpi icon="lock" value={failedCount} label="Failed" tone="danger" />
        <Kpi icon="database" value={reindexStatus} label="Index status" tone="success" />
      </div>
      {message && <p className="success panel-message">{message}</p>}
      {error && <p className="error panel-error">{error}</p>}
      <div className="knowledge-grid">
        <SectionCard>
          <h2>Upload policy PDF</h2>
          <p className="section-note">PDF sẽ được ingest vào Azure Search sau khi upload xong.</p>
          <label className="dropzone">
            <Icon name="upload" />
            <strong>{pdfFile ? pdfFile.name : 'Kéo thả file PDF vào đây hoặc bấm để chọn file'}</strong>
            <span>Chỉ hỗ trợ .pdf</span>
            <input
              type="file"
              accept="application/pdf,.pdf"
              onChange={(e) => {
                setPdfFile(e.target.files?.[0] ?? null);
                if (error) setError('');
              }}
            />
          </label>
          <label>
            <span>Tên hiển thị</span>
            <input
              value={pdfTitle}
              onChange={(e) => setPdfTitle(e.target.value)}
              placeholder={pdfFile ? pdfFile.name.replace(/\.pdf$/i, '') : 'Ví dụ: VPN Reset Policy'}
            />
          </label>
          <div className="actions split">
            <button className="primary" type="button" onClick={uploadPdf} disabled={uploading}>
              <Icon name="upload" /> {uploading ? 'Đang đọc PDF...' : 'Upload PDF'}
            </button>
            <button className="secondary" type="button" onClick={reindex}>
              ↻ Re-index all
            </button>
          </div>
        </SectionCard>

        <SectionCard>
          <h2>Thêm text knowledge thủ công</h2>
          <div className="form-grid two compact">
            <label>
              <span>Tiêu đề</span>
              <input value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Ví dụ: Quy trình reset VPN" />
            </label>
            <label>
              <span>Danh mục</span>
              <select value={category} onChange={(e) => setCategory(e.target.value)}>
                {categories.map((item) => (
                  <option key={item}>{item}</option>
                ))}
              </select>
            </label>
          </div>
          <label>
            <span>Nội dung</span>
            <textarea value={content} onChange={(e) => setContent(e.target.value)} placeholder="Nhập nội dung tài liệu nội bộ..." />
          </label>
          <label>
            <span>Source URL</span>
            <input value={sourceUrl} onChange={(e) => setSourceUrl(e.target.value)} placeholder="internal://it/vpn-reset" />
          </label>
          <div className="actions">
            <button className="primary" type="button" onClick={addDoc}>
              Thêm tài liệu
            </button>
          </div>
        </SectionCard>
      </div>
      <SectionCard>
        <h2>File AI đã đọc</h2>
        {docs.length === 0 ? (
          <EmptyState title="Không có document" text="Tài liệu ingest sẽ xuất hiện tại đây." />
        ) : (
          <div className="knowledge-list">
            {docs.map((document) => (
              <div className="knowledge-item" key={document.id}>
                <Icon name="file" />
                <div>
                  <strong>{document.title}</strong>
                  <span>
                    ID: {document.id} · Danh mục: {document.category}
                    {document.fileName ? ` · File: ${document.fileName}` : ''}
                    {document.sourceUrl ? ` · Source: ${document.sourceUrl}` : ''}
                  </span>
                </div>
                <span className={`badge status-${document.ingestionStatus.toLowerCase()}`}>
                  {document.ingestionStatus === 'Ready' ? 'Sẵn sàng tư vấn' : document.ingestionStatus}
                </span>
                <span className="date">Cập nhật: {formatDate(document.updatedAt)}</span>
                <button className="delete-button" type="button" onClick={() => deleteDoc(document)}>
                  Xóa
                </button>
              </div>
            ))}
          </div>
        )}
      </SectionCard>
    </>
  );
}
