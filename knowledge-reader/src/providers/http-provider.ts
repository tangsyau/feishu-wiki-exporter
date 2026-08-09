import type {
  KnowledgeManifest,
  KnowledgePage,
  KnowledgeSearchIndex,
  KnowledgeTreeNode
} from "../types";
import type { KnowledgeProvider } from "./knowledge-provider";

export class HttpKnowledgeProvider implements KnowledgeProvider {
  readonly mode = "http" as const;

  constructor(private readonly baseUrl = "./knowledge") {}

  async chooseKnowledge(): Promise<boolean> {
    return this.tryLoadDefault();
  }

  async tryLoadDefault(): Promise<boolean> {
    try {
      await this.loadManifest();
      return true;
    } catch {
      return false;
    }
  }

  loadManifest(): Promise<KnowledgeManifest> {
    return this.readJson<KnowledgeManifest>("manifest.json");
  }

  loadTree(): Promise<KnowledgeTreeNode> {
    return this.readJson<KnowledgeTreeNode>("tree.json");
  }

  loadSearchIndex(): Promise<KnowledgeSearchIndex> {
    return this.readJson<KnowledgeSearchIndex>("index/search-index.json");
  }

  loadPage(relativePath: string): Promise<KnowledgePage> {
    return this.readJson<KnowledgePage>(relativePath);
  }

  async openOriginal(relativePath: string): Promise<void> {
    const url = this.url(relativePath);
    window.open(url, "_blank", "noopener,noreferrer");
  }

  private async readJson<T>(relativePath: string): Promise<T> {
    const response = await fetch(this.url(relativePath), { cache: "no-store" });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}: ${relativePath}`);
    }
    return response.json() as Promise<T>;
  }

  private url(relativePath: string): string {
    const encoded = relativePath.split("/").map(encodeURIComponent).join("/");
    return `${this.baseUrl.replace(/\/$/, "")}/${encoded}`;
  }
}
