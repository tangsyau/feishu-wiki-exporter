import type {
  KnowledgeManifest,
  KnowledgePage,
  KnowledgeSearchIndex,
  KnowledgeTreeNode
} from "../types";
import type { KnowledgeProvider } from "./knowledge-provider";

interface LegacyTauriApi {
  tauri: {
    invoke<T>(command: string, args?: Record<string, unknown>): Promise<T>;
  };
  dialog: {
    open(options: {
      directory: boolean;
      multiple: boolean;
      title: string;
    }): Promise<string | string[] | null>;
  };
}

declare global {
  interface Window {
    __TAURI__?: LegacyTauriApi;
  }
}

export class LegacyTauriKnowledgeProvider implements KnowledgeProvider {
  readonly mode = "tauri" as const;

  async chooseKnowledge(): Promise<boolean> {
    const selected = await this.api.dialog.open({
      directory: true,
      multiple: false,
      title: "选择离线知识库目录"
    });
    if (typeof selected !== "string") {
      return false;
    }
    await this.invoke("set_knowledge_root", { path: selected });
    return true;
  }

  tryLoadDefault(): Promise<boolean> {
    return this.invoke<boolean>("try_load_default_knowledge");
  }

  async rememberKnowledge(): Promise<void> {
    await this.invoke("remember_knowledge_root");
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

  loadAssetData(relativePath: string): Promise<string> {
    return this.invoke<string>("read_knowledge_asset", { relativePath });
  }

  async openOriginal(relativePath: string): Promise<void> {
    await this.invoke("open_original", { relativePath });
  }

  private get api(): LegacyTauriApi {
    const api = window.__TAURI__;
    if (!api) {
      throw new Error("Tauri 1 接口尚未就绪。");
    }
    return api;
  }

  private invoke<T>(command: string, args?: Record<string, unknown>): Promise<T> {
    return this.api.tauri.invoke<T>(command, args);
  }

  private async readJson<T>(relativePath: string): Promise<T> {
    const json = await this.invoke<string>("read_knowledge_text", { relativePath });
    return JSON.parse(json) as T;
  }
}
