import { defineConfig } from "vite";

export default defineConfig({
  base: "./",
  clearScreen: false,
  build: {
    // UOS V20 常见的 WebKitGTK 4.0 内核早于现代 Chromium，显式降级语法目标。
    target: "safari13"
  },
  server: {
    host: "127.0.0.1",
    port: 1420,
    strictPort: true
  }
});
