export interface KnowledgeDocument {
  id: string;
  title: string;
  kind: "docx" | "pdf" | "spreadsheet" | "image" | "file";
  relativePath: string;
  originalPath: string | null;
  pagePath: string | null;
  breadcrumb: string;
}

export interface KnowledgeStatistics {
  pages: number;
  attachments: number;
  unsupportedBlocks: number;
}

export interface KnowledgeManifest {
  format: "feishu-offline-knowledge";
  version: number;
  name: string;
  generatedUtc: string;
  documents: KnowledgeDocument[];
  statistics?: KnowledgeStatistics;
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
  html?: string;
  text: string;
  blocks?: KnowledgeBlock[];
  unsupportedBlockCount?: number;
}

export interface KnowledgeInline {
  text: string;
  bold: boolean;
  italic: boolean;
  underline: boolean;
  strike: boolean;
  code: boolean;
  url: string | null;
  targetPageId: string | null;
}

export interface KnowledgeLink {
  title: string;
  targetPageId: string;
  anchor?: string | null;
}

export interface KnowledgeBlock {
  id: string;
  type: string;
  text?: string | null;
  level?: number | null;
  checked?: boolean | null;
  language?: string | null;
  sequence?: string | null;
  assetPath?: string | null;
  fileName?: string | null;
  url?: string | null;
  sourceType?: number | null;
  inlines: KnowledgeInline[];
  links: KnowledgeLink[];
  children: KnowledgeBlock[];
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
