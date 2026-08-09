# Feishu Wiki Reader

飞书知识库离线阅读器源码。它使用 Tauri + TypeScript/Vite 显示由“飞书知识库导出助手”生成的 `feishu-offline-knowledge` 目录。数据格式参见[离线知识库设计](../docs/离线知识库设计.md)。

阅读器与 Exporter 共用仓库根目录的 [VERSION](../VERSION)。目录栏底部会显示当前版本；发布前可运行 `node ../scripts/check-version.mjs` 检查 npm、Rust 与 Tauri 元数据是否一致。

当前支持：

- 按飞书页面节点层级浏览，不把知识库强制拆成文件夹和文档；
- 页面可以同时打开正文和展开子页面，纯导航页只负责展开；
- 保留飞书知识库中的同级排列顺序，不改动实际文件名；
- 标题搜索；
- DOCX 文本的中文二元组全文索引；
- 从搜索结果打开文档后，可返回原结果列表，并恢复关键词和滚动位置；
- DOCX 网页化阅读，包括常见标题、段落、列表、表格和内嵌图片；
- PDF、XLSX 和其他附件保留原文件并调用系统默认程序打开。

Web 前端通过 `KnowledgeProvider` 抽象读取内容。Tauri 模式使用受限制的本地文件命令；非 Tauri 模式会从 `./knowledge/` 通过 HTTP 读取同一数据格式，仅为以后的内网部署保留接口，当前不提供服务器部署流程。

## 前端开发

```bash
npm install
npm run build
```

## Tauri

安装 Rust 和 Tauri 所需的平台依赖后：

```bash
npm run tauri dev
npm run tauri build
```

正式发布时，Windows x64 版采用 Portable ZIP，不生成 NSIS 安装程序；Linux x64 与 ARM64 均同时提供 AppImage、DEB 和 RPM。Windows Portable 由 GitHub Actions 使用 `--no-bundle` 构建，解压后直接运行 `FeishuWikiReader.exe`。

Linux AppImage 适合无需安装的通用场景，但它会捆绑 WebKitGTK 等运行时。DEB / RPM 改用目标系统的 WebKitGTK，包更小，也更适合排查 AppImage 与特定发行版图形栈的兼容问题。Debian、Ubuntu、Deepin 优先测试 DEB；Fedora、RHEL 系优先测试 RPM。

Fedora / RHEL 系安装示例：

```bash
sudo dnf install ./Feishu*.rpm
```

Debian / Ubuntu / Deepin 安装示例：

```bash
sudo apt install ./Feishu*.deb
```

macOS 暂不作为正式支持平台，也不再生成 Reader 发布包；以后如有明确使用需求，再重新进行签名、公证和实机兼容测试。
