import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import type {
  KnowledgeManifest,
  KnowledgePage,
  KnowledgeSearchIndex,
  KnowledgeTreeNode
} from "../types";
import type { KnowledgeProvider } from "./knowledge-provider";

export class TauriKnowledgeProvider implements KnowledgeProvider {
  readonly mode = "tauri" as const;

  async chooseKnowledge(): Promise<boolean> {
    const selected = await open({
      directory: true,
      multiple: false,
      title: "选择离线知识库目录"
    });
    if (typeof selected !== "string") {
      return false;
    }
    await invoke("set_knowledge_root", { path: selected });
    return true;
  }

  async tryLoadDefault(): Promise<boolean> {
    return invoke<boolean>("try_load_default_knowledge");
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
    await invoke("open_original", { relativePath });
  }

  private async readJson<T>(relativePath: string): Promise<T> {
    const json = await invoke<string>("read_knowledge_text", { relativePath });
    return JSON.parse(json) as T;
  }
}
