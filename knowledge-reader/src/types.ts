export interface KnowledgeDocument {
  id: string;
  title: string;
  kind: "docx" | "pdf" | "spreadsheet" | "image" | "file";
  relativePath: string;
  originalPath: string;
  pagePath: string | null;
  breadcrumb: string;
}

export interface KnowledgeManifest {
  format: "feishu-offline-knowledge";
  version: number;
  name: string;
  generatedUtc: string;
  documents: KnowledgeDocument[];
}

export interface KnowledgeTreeNode {
  title: string;
  type: "folder" | "document" | "page";
  documentId: string | null;
  kind: string | null;
  children: KnowledgeTreeNode[];
}

export interface KnowledgePage {
  title: string;
  html: string;
  text: string;
}

export interface KnowledgeSearchIndex {
  version: number;
  postings: Record<string, string[]>;
}

export interface SearchResult {
  document: KnowledgeDocument;
  snippet: string;
  score: number;
  titleMatched: boolean;
}
