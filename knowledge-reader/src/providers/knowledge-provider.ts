import type {
  KnowledgeManifest,
  KnowledgePage,
  KnowledgeSearchIndex,
  KnowledgeTreeNode
} from "../types";

export interface KnowledgeProvider {
  readonly mode: "tauri" | "http";
  chooseKnowledge(): Promise<boolean>;
  tryLoadDefault(): Promise<boolean>;
  loadManifest(): Promise<KnowledgeManifest>;
  loadTree(): Promise<KnowledgeTreeNode>;
  loadSearchIndex(): Promise<KnowledgeSearchIndex>;
  loadPage(relativePath: string): Promise<KnowledgePage>;
  openOriginal(relativePath: string): Promise<void>;
}
