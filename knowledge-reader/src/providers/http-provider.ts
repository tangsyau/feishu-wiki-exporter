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

  async loadAssetData(relativePath: string): Promise<string> {
    const response = await fetch(this.url(relativePath));
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}: ${relativePath}`);
    }
    const original = await response.blob();
    const header = new Uint8Array(await original.slice(0, 16).arrayBuffer());
    const blob = new Blob([original], { type: detectImageMime(header, original.type) });
    return await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(blob);
    });
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

function detectImageMime(bytes: Uint8Array, fallback: string): string {
  if (bytes.length >= 4 && bytes[0] === 0x89 && bytes[1] === 0x50 && bytes[2] === 0x4e && bytes[3] === 0x47) return "image/png";
  if (bytes.length >= 3 && bytes[0] === 0xff && bytes[1] === 0xd8 && bytes[2] === 0xff) return "image/jpeg";
  if (bytes.length >= 4 && String.fromCharCode(...bytes.slice(0, 4)) === "GIF8") return "image/gif";
  if (bytes.length >= 12 && String.fromCharCode(...bytes.slice(0, 4)) === "RIFF" && String.fromCharCode(...bytes.slice(8, 12)) === "WEBP") return "image/webp";
  return fallback || "application/octet-stream";
}
