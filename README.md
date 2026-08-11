# 飞书知识库导出助手（Feishu Wiki Exporter）

一款开源、跨平台的飞书知识库与云空间文件夹导出工具，当前正式发布 Windows 和 Linux 版本。

本项目为非官方开源工具，与飞书官方无隶属或合作关系。

## 项目来源

本项目基于 [eternalfree/feishu-doc-export](https://github.com/eternalfree/feishu-doc-export) 的 Apache License 2.0 源码重构。感谢原作者公开项目和实现思路。

本项目是独立维护的衍生版本，并非原项目的官方后续版本。它保留“遍历目录 → 创建飞书官方导出任务 → 下载到本地”的核心路径，重新实现了跨平台桌面界面、增量状态、附件处理、离线阅读器和发布流程，并移除了商业 Aspose 依赖及 DOCX 转 Markdown 功能。

## 功能

- 导出完整知识库，保留原有层级；
- 导出指定云空间文件夹及其子文件夹；
- 飞书文档导出为 DOCX 或 PDF；
- 电子表格、多维表格导出为 XLSX；
- 下载目录中的普通附件，以及新版文档内嵌的 PDF 等文件；
- 可选择把内嵌附件放在主文档同一目录，或放入主文档同名子文件夹；
- 分析有子文档页面的实际内容，导出前生成疑似导航页清单供逐项确认；
- 读取目录和内嵌附件时显示扫描进度；
- 可选择只生成 Reader 离线包、只生成 Office 文档，或同时生成两者；
- Reader 离线包直接保存飞书页面块结构，保留换行、子页面目录、内部跳转和中文全文索引；
- Reader 包不重复保存正文 DOCX，只保留页面数据和阅读所需图片、附件；
- Reader 与 Office 使用独立 JSON 增量状态，支持中断后继续；
- `.part` 临时文件，防止中断后留下损坏文件；
- API 统一限速、429/5xx 指数退避、导出任务总超时；
- 跨平台文件名处理；长名称按 UTF-8 字节数缩短，同目录重名使用 `（2）`、`（3）`；
- CSV 结果报告；
- 现代化三步桌面界面，将连接飞书、选择内容和导出设置分开呈现；
- 内置 Noto Sans SC 界面字体，统一 Windows 和 Linux 的主要字形；
- 使用可缩放布局和布局取整，适配 HiDPI 与较高系统缩放比例；
- 原创应用图标，窗口与 Windows 发布文件统一使用；
- App Secret 只在内存中使用，不写入配置文件。

暂不支持 Markdown、飞书画板、思维笔记等需要单独解析的类型。

## 项目要求

- 开发：.NET 10 SDK；
- 运行发布包：不需要预装 .NET（使用 self-contained 发布）；
- 桌面界面：Avalonia 12.1；
- 增量状态：纯 .NET JSON，不依赖 SQLite 原生库。

## 版本与更新

Exporter 和 Reader 使用统一的语义化版本号。当前版本会显示在 Exporter 左侧栏底部和 Reader 目录栏底部，也会写入 Windows 文件属性、Linux 软件包元数据和发布文件名。

本项目暂不联网检查或自动安装更新。用户可以将程序中显示的版本与 [GitHub Releases](https://github.com/tangsyau/feishu-wiki-exporter/releases) 中的最新版比较；例如 `0.1.1` 比 `0.1.0` 更新。版本变化说明记录在 [CHANGELOG.md](CHANGELOG.md)。

仓库根目录的 [VERSION](VERSION) 是版本号的唯一来源。维护者更新版本和创建 Release 的步骤参见[发布指南](docs/发布指南.md)。

## 文档

- [飞书应用配置指南](docs/飞书应用配置指南.md)：创建应用、开通权限并授权知识库；
- [离线知识库设计](docs/离线知识库设计.md)：离线包格式、全文索引和 Reader 分工；
- [发布指南](docs/发布指南.md)：统一版本号、Git 标签和 GitHub Release 流程；
- [发布前测试清单](docs/发布前测试清单.md)：供维护者在发布二进制包前进行跨平台回归测试。

## 飞书应用准备

第一次使用请先阅读：[飞书应用配置指南](docs/飞书应用配置指南.md)。

1. 在飞书开放平台创建企业自建应用；
2. 开通读取知识库、读取新版文档、导出云文档和下载文件/素材所需权限；
3. 发布应用并由企业管理员审核；
4. 在知识空间设置中直接把应用添加为可阅读成员；
5. 记录 App ID 和 App Secret。

建议先用只包含少量测试文档的知识库验证权限，然后再导出正式知识库。

## 构建

```bash
dotnet restore FeishuExporter.sln
dotnet build FeishuExporter.sln -c Release
dotnet test FeishuExporter.sln -c Release
```

桌面版：

```bash
dotnet run --project src/FeishuExporter.Desktop
```

跨平台界面和发布包验证请参考：[发布前测试清单](docs/发布前测试清单.md)。

## 导出目录

```text
选择的导出根目录/
├─ 知识库名称/                 # 选择 Office 或两者时生成
│  ├─ 总览.docx
│  ├─ 参考资料.pdf
│  ├─ 总览/
│  │  └─ 子文档.docx
│  ├─ 数据表.xlsx
│  └─ export-report.csv
├─ 知识库名称-offline/         # 选择 Reader 或两者时生成
└─ .feishu-exporter-state/     # 独立增量状态，请勿删除
   └─ <来源标识>/
      ├─ source.json
      ├─ office-state.json
      ├─ reader-state.json
      ├─ order.json
      ├─ page-cache/
      └─ asset-cache/
```

不要删除导出根目录中的 `.feishu-exporter-state`。它按飞书来源分别保存 Reader 与 Office 的增量状态；删除某一种输出目录不会影响另一种状态。0.2.0 首次运行时会尝试复制旧版 Office 状态，旧文件不会被自动删除。Office 目录内仍可能保留 `.feishu-export` 诊断和历史导航页备份，用于兼容与排错。

“内嵌附件位置”默认选择“与主文档同一目录”。例如主文档为 `abc.docx`、内嵌附件为 `abc.pdf`，两者会并列保存。也可以改为“主文档同名子文件夹”，恢复为 `abc/abc.pdf` 的布局。

“识别疑似导航页，并在导出前确认跳过清单”默认开启。程序只分析确实有子文档的新版文档，并按照飞书块的父子树而不是扁平列表统计正文：导航组件、表格等容器内部的后代文字不会脱离容器重复计数。连续出现至少 3 个非空正文块、正文累计达到约 100 字，或包含真正的表格、图片、附件等明确内容时，页面会判定为有实际内容；标题、空行、分割线以及新旧版子页面列表不算正文。

审核采用三级分类：“高度疑似导航页”默认勾选跳过；未知块、分析失败、用途无法确认的小组件或疑似由页面链接组成的表格归入“无法确定”，显示在同一清单中但默认保留；明确有正文的页面不进入跳过清单。对于由至少两组标题和子页面目录对应组成、且没有其他实际内容的页面，40 号 AddOns 小组件按目录组件处理。用户仍可逐项改变选择。每次确认导出后，详细的块类型数量、小组件类型、正文指标、分类理由和错误会以可直接阅读的中文写入 `.feishu-export/navigation-analysis.json`，用于排查误判；该文件不包含 App Secret。

例如“公务用车”下面存在“申请流程”等子文档，而“公务用车”页面只有标题和子页面列表时，确认跳过后仍会保留 `公务用车/` 目录层级，但不会额外生成 `公务用车.docx`。判断依据来自飞书文档块和真实父子关系，而不是本地同名文件夹，因此附件形成的同名目录不会触发该规则。状态文件能够确认来源的历史导航页会移入 `.feishu-export/retired-navigation-pages/` 作为内部备份；无法确认来源的文件不会自动移动或删除。

## 离线知识库

在“导出设置”中选择默认的“Reader 离线包”，程序会直接从飞书页面块生成 `<知识库名>-offline`，不需要先生成 DOCX。

离线包的格式与阅读器平台无关，主要包含：

```text
<知识库名>-offline/
├─ manifest.json
├─ tree.json
├─ index/
│  └─ search-index.json
├─ diagnostics/
│  ├─ unsupported-blocks.json
│  └─ subpage-link-resolution.json
├─ pages/
│  └─ <文档ID>.json
└─ assets/
   ├─ images/
   ├─ attachments/
   └─ files/
```

version 3 直接把飞书块规范化为页面 JSON，保留段落换行、标题、列表、引用、代码、图片、附件、页面目录和子页面目录。每个飞书节点都是可打开的页面，同时可以拥有子页面；纯导航页也完整保留，其子页面标题能够在 Reader 内部跳转。多层导航页会根据目录块的 `wiki_token` 和完整知识树还原目标分类下的实际子页面。导航页分析只用于 Office 导出，不会删除 Reader 页面。

知识树保留飞书原有父子关系和同级顺序，实际文件名不添加序号。Reader 会记住最后一次成功打开的知识库；页面顶部面包屑可以返回任意上级，左侧目录会自动展开并定位当前页面。搜索结果采用严格分组：标题命中始终排在仅正文命中之前；从搜索结果打开页面后，可以返回原结果并恢复关键词和滚动位置。PDF、XLSX 和其他无法直接呈现的文件作为必要资源保留，可调用系统默认程序打开。扫描版 PDF 的 OCR 当前不在支持范围。Reader 仍可打开旧的 version 1、version 2 离线包。

更详细的格式说明参见：[离线知识库设计](docs/离线知识库设计.md)。

`knowledge-reader/` 是独立的 Tauri + TypeScript/Vite 阅读器源码。Web 界面通过数据提供器抽象读取知识库：当前桌面版从 Tauri 的受限本地文件接口读取；同一前端已保留从 `./knowledge/` 读取静态 HTTP 内容的实现，但当前不提供内网服务器部署。

阅读器与知识库数据相互独立。普通员工只需准备一次阅读器程序；管理员以后重新导出并生成新的 `<知识库名>-offline` 后，只需重新分发这份知识库目录，不需要重新编译阅读器。

## 发布

GitHub Actions 会先为每个平台生成多文件、自包含的底层产物，然后再封装为适合用户下载的格式：

推送与 `VERSION` 对应的标签（例如 `v0.1.0`）后，`release` workflow 会自动调用 Exporter 和 Reader 构建、汇总 15 个平台包、生成 `SHA256SUMS.txt`，并创建已经附带全部文件和发行说明的 Draft Release。维护者检查无误后只需点击 **Publish release**，不需要逐个下载和重新上传 Actions 产物。具体步骤参见[发布指南](docs/发布指南.md)。

| 平台 | Actions 产物 | 面向用户的格式 |
|---|---|---|
| Windows x64 / ARM64 | `feishu-wiki-exporter-<版本>-win-*` | ZIP，内含单文件 `FeishuWikiExporter.exe` 及许可证文档 |
| 常规 Linux x64 / ARM64 | `feishu-wiki-exporter-<版本>-linux-*-appimage` | AppImage，普通桌面 Linux 用户首选 |
| 常规 Linux x64 / ARM64 | `feishu-wiki-exporter-<版本>-linux-*-portable` | 多文件 Portable TAR.GZ，AppImage 无法运行时使用 |
| Alpine Linux x64 / ARM64 | `feishu-wiki-exporter-<版本>-linux-musl-*-portable` | 多文件 Portable TAR.GZ |

阅读器由独立的 `reader` workflow 构建。Windows x64 提供免安装 Portable ZIP；Linux x64 / ARM64 各提供 AppImage、DEB 和 RPM。AppImage 继续作为免安装通用版，DEB / RPM 则使用目标系统的 WebKitGTK，优先用于 Debian/Deepin 与 Fedora/RHEL 等对应发行版。阅读器不包含任何 App ID、App Secret 或企业知识库内容；知识库数据由管理员另外分发。

macOS 当前暂停正式支持，Exporter 与 Reader 的 Actions 均不再生成 macOS 发布包。以后如有明确需求，再重新进行实机兼容、签名和公证工作。

常规 Linux 指使用 glibc 的发行版，例如 Ubuntu、Debian、Fedora、统信等。Alpine 等使用 musl 的发行版必须选择 `linux-musl-*`，不能使用 AppImage 版。

AppImage 下载后需要赋予执行权限：

```bash
chmod +x feishu-wiki-exporter-0.1.0-linux-x64.AppImage
./feishu-wiki-exporter-0.1.0-linux-x64.AppImage
```

Portable 版必须完整解压并保留目录内全部文件，不能只复制 `FeishuWikiExporter`。TAR.GZ 会保留 Linux 执行权限：

```bash
tar -xzf feishu-wiki-exporter-0.1.0-linux-x64-portable.tar.gz
cd feishu-wiki-exporter-0.1.0-linux-x64
./FeishuWikiExporter
```

界面内置的 Noto Sans SC 字体遵循 SIL Open Font License 1.1。各平台发布包会一并附带 `NotoSansSC-OFL-1.1.txt`；该字体不因本项目采用 Apache License 2.0 而改变其自身许可证。

本地开发时可使用以下脚本生成六个平台的多文件底层产物。Linux：

```bash
chmod +x scripts/publish.sh
./scripts/publish.sh
```

Windows PowerShell：

```powershell
.\scripts\publish.ps1
```

这些底层产物主要用于平台封装和故障排查；普通用户应优先下载上表中的正式封装包。所有格式均为 self-contained，运行时不需要预装 .NET。

## 安全说明

- 桌面版只在内存中保留 App Secret，关闭程序后即丢失；
- 不要把 App Secret 放进脚本、Git 仓库或截图；
- 导出目录可能包含企业内部资料，请自行设置磁盘权限、加密与备份策略。
- 离线知识库包含原始文件、可阅读页面和搜索数据，应与原始企业文档采用同等级别的访问控制；分发给其他员工时请使用公司认可的内部分发渠道。

## 已知限制

- 尚未实现操作系统钥匙串安全保存；
- 尚未实现导出前完整预检；
- 附件沿用飞书返回的文件名；
- 只把真正的文件块作为内嵌附件下载；指向其他云文档的链接卡片暂不递归下载；
- 不支持 Markdown；
- 离线阅读器对飞书新版文档的规范化文本建立全文索引；PDF、表格和其他附件可以按标题查找并打开原文件，但暂不解析其正文，也不做 OCR；
- 第一次使用仍需在飞书开放平台正确配置权限。

## 许可证

本项目及其所基于的原项目均采用 Apache License 2.0。参见 [LICENSE](LICENSE) 与 [NOTICE](NOTICE)。原项目地址：[eternalfree/feishu-doc-export](https://github.com/eternalfree/feishu-doc-export)。
