# Reader WebKitGTK 4.0 试验版

该试验版面向 UOS V20、旧版 Deepin 等提供 `libwebkit2gtk-4.0`、但没有 `libwebkit2gtk-4.1` 的 Linux 系统。它不会替换正式的 Tauri 2 / WebKitGTK 4.1 Reader，也不会自动加入 GitHub Release。

## 构建方式

1. 打开仓库的 **Actions** 页面；
2. 选择 **Reader WebKitGTK 4.0 Experimental**；
3. 点击 **Run workflow**；
4. 构建结束后，根据设备下载名称含 `linux-x64` 或 `linux-arm64` 的试验产物；
5. UOS、Deepin、Debian 优先测试 DEB，AppImage 作为免安装对照版本。

工作流在 Debian 10 容器中原生编译两个架构，并自动确认：

- 二进制链接 `libwebkit2gtk-4.0.so.37`；
- 二进制没有链接 WebKitGTK 4.1；
- 所需最高 glibc 符号不超过 `GLIBC_2.28`；
- DEB 明确声明依赖 `libwebkit2gtk-4.0-37`。

校验步骤会在日志中列出实际生成的文件、程序动态依赖、DEB 架构与依赖、最高 glibc 符号。若校验失败，Actions 页面会额外出现名称含 `unverified-diagnostics` 的诊断包，其中的 `webkit4-verification.txt` 会说明具体失败项。该诊断包没有通过兼容性校验，只用于排查，不应安装或分发。

## 安装和运行

确认系统架构和 WebKitGTK：

```bash
uname -m
ldconfig -p | grep 'libwebkit2gtk-4.0.so.37'
```

安装 DEB：

```bash
sudo apt install ./Feishu_Wiki_Reader_WebKitGTK_4.0_Experimental_*.deb
```

运行 AppImage：

```bash
chmod +x ./*WebKitGTK*4.0*.AppImage
./*WebKitGTK*4.0*.AppImage
```

AppImage 不会内置完整的 WebKitGTK，系统仍须存在 4.0 运行库。不要从其他 Debian 或 Ubuntu 版本强行安装 WebKitGTK、glibc 或 GLib 包。

## 必测项目

- 首次启动能够显示完整界面，没有白屏或灰屏；
- 能够弹出目录选择窗口并打开 version 1—3 的离线知识库；
- 重启后自动打开上次使用的知识库，也能切换到另一个目录；
- 中文标题、正文、目录树、面包屑和全文搜索显示正常；
- 有序列表、内部页面跳转、图片和长页面滚动正常；
- DOCX、PDF、XLSX 等附件能够交给系统默认程序打开；
- 高分辨率缩放下文字没有截断，较大知识库浏览时没有明显卡死；
- 关闭后没有残留命令行窗口。

试验版和正式版使用相同的应用标识，因此会共享 `reader-settings.json` 中保存的上次知识库路径。它们只读取离线包，不会修改知识库内容。
