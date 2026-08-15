# WebKitGTK 4.0 兼容外壳

本目录用于构建 UOS V20、旧版 Deepin 等仍提供 `libwebkit2gtk-4.0` 的 Linux 兼容版。常规 Reader 位于 `knowledge-reader/src-tauri`，使用 Tauri 2 和 WebKitGTK 4.1。

兼容版与常规版共用同一套前端和离线知识库格式，支持选择目录、记住上次知识库、全文搜索和打开原始附件。两者使用相同的应用标识，因此可以共享已经保存的知识库路径。

GitHub Actions 的 `Reader WebKitGTK 4.0 Compatibility` 会生成 x64 与 ARM64 AppImage，并将它们加入正式 Release。构建过程同时生成 DEB 作为链接和依赖校验载体，但不向普通用户发布。
