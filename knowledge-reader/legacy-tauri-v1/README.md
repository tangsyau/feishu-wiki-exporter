# WebKitGTK 4.0 试验外壳

本目录只用于验证 UOS V20、旧版 Deepin 等仍提供 `libwebkit2gtk-4.0` 的 Linux 系统。正式 Reader 继续位于 `knowledge-reader/src-tauri`，仍使用 Tauri 2 和 WebKitGTK 4.1。

试验版与正式版共用同一套前端和离线知识库格式，支持选择目录、记住上次知识库、全文搜索和打开原始附件。两者使用相同的应用标识，因此可以共享已经保存的知识库路径。

在 GitHub Actions 中手动运行 `Reader WebKitGTK 4.0 Experimental`，可得到 x64 与 arm64 的 DEB 和 AppImage。试验包不会自动进入正式 Release。
