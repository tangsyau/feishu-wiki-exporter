import assert from "node:assert/strict";
import test from "node:test";
import { LegacyTauriKnowledgeProvider } from "../src/providers/legacy-tauri-provider.ts";

test("Tauri 1 provider 使用全局接口选择并读取知识库", async () => {
  const calls: Array<{ command: string; args?: Record<string, unknown> }> = [];
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      __TAURI__: {
        dialog: {
          open: async () => "/tmp/offline-knowledge"
        },
        tauri: {
          invoke: async (command: string, args?: Record<string, unknown>) => {
            calls.push({ command, args });
            if (command === "try_load_default_knowledge") return true;
            if (command === "read_knowledge_text") {
              return JSON.stringify({ format: "feishu-offline-knowledge", version: 3 });
            }
            return undefined;
          }
        }
      }
    }
  });

  const provider = new LegacyTauriKnowledgeProvider();
  assert.equal(await provider.chooseKnowledge(), true);
  assert.equal(await provider.tryLoadDefault(), true);
  const manifest = await provider.loadManifest();
  assert.equal(manifest.format, "feishu-offline-knowledge");
  assert.deepEqual(calls[0], {
    command: "set_knowledge_root",
    args: { path: "/tmp/offline-knowledge" }
  });
  assert.deepEqual(calls[2], {
    command: "read_knowledge_text",
    args: { relativePath: "manifest.json" }
  });

  Reflect.deleteProperty(globalThis, "window");
});

test("Tauri 1 provider 在取消目录选择时不修改知识库", async () => {
  let invoked = false;
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      __TAURI__: {
        dialog: { open: async () => null },
        tauri: {
          invoke: async () => {
            invoked = true;
          }
        }
      }
    }
  });

  assert.equal(await new LegacyTauriKnowledgeProvider().chooseKnowledge(), false);
  assert.equal(invoked, false);
  Reflect.deleteProperty(globalThis, "window");
});
