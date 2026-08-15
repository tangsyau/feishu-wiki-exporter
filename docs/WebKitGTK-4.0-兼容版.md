# Reader WebKitGTK 4.0 兼容版

WebKitGTK 4.0 兼容版面向 UOS V20、旧版 Deepin 等提供 `libwebkit2gtk-4.0`、但没有 `libwebkit2gtk-4.1` 的 Linux 系统。它与常规 WebKitGTK 4.1 Reader 使用相同的界面、离线知识库格式和配置目录。

## 如何选择

- 系统提供 `libwebkit2gtk-4.1`：优先使用常规 Reader 的 AppImage、DEB 或 RPM；
- 系统只有 `libwebkit2gtk-4.0.so.37`：使用文件名含 `webkitgtk4.0` 的 AppImage；
- 不要为了运行 Reader 而从其他发行版强行混装 WebKitGTK、glibc 或 GLib。

确认系统架构和运行库：

```bash
uname -m
ldconfig -p | grep 'libwebkit2gtk-4.0.so.37'
```

## 运行

从 GitHub Release 下载与架构对应的文件：

```text
feishu-wiki-reader-<版本>-linux-x64-webkitgtk4.0.AppImage
feishu-wiki-reader-<版本>-linux-arm64-webkitgtk4.0.AppImage
```

赋予执行权限后运行：

```bash
chmod +x feishu-wiki-reader-*-webkitgtk4.0.AppImage
./feishu-wiki-reader-*-webkitgtk4.0.AppImage
```

兼容版针对 WebKitGTK 4.0 环境构建。即使采用 AppImage，仍会使用系统图形栈；若无法启动，不要通过混装其他发行版的 WebKitGTK、glibc 或 GLib 强行解决。

## 构建与校验

维护者可以在 Actions 中手动运行 **Reader WebKitGTK 4.0 Compatibility**。正式发布标签也会自动调用同一工作流，原生生成 x64 与 ARM64 AppImage。

工作流使用 Tauri 1 和 Debian 10 / glibc 2.28 构建，并额外生成一个不发布的 DEB 作为校验载体，自动确认：

- 实际安装程序链接 `libwebkit2gtk-4.0.so.37`；
- 没有链接 WebKitGTK 4.1；
- 所需最高 glibc 符号不超过 `GLIBC_2.28`；
- 校验载体声明依赖 `libwebkit2gtk-4.0-37`。

若校验失败，Actions 会生成名称含 `unverified-diagnostics` 的诊断包。该包没有通过兼容性检查，只用于排查，不应安装或分发。

## 使用限制

兼容版与常规版共享 `reader-settings.json` 中保存的上次知识库路径。两者都只读取离线包，不会修改知识库内容。由于 WebKitGTK 4.0 较旧，发布前仍应在目标发行版上检查界面、目录选择、全文搜索、附件打开、长页面滚动和高分屏显示。
