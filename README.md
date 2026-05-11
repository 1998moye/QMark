# QMark

A fast, beautiful Markdown editor for Windows built with C# WPF and WebView2.

一款基于 C# WPF 和 WebView2 打造的快速、美观的 Windows Markdown 编辑器。

[English](#english) | [中文](#中文)

---

## English

### Features

- **Real-time Preview** — Instant Markdown rendering powered by WebView2
- **Split View** — Side-by-side editor and preview, or full-source / full-preview modes
- **Three Themes** — Light, Dark, and Sepia with smooth transitions
- **File Tree** — Built-in sidebar with file explorer and auto-generated outline (table of contents)
- **Window Resize** — Drag any edge or corner to freely resize the window
- **Paste as Markdown** — Paste content from web pages (e.g. ChatGPT) and retain Markdown formatting
- **File Association** — Open `.md` files directly with QMark
- **Recently Opened** — Quick access to recent files on startup

### Prerequisites

Before building, make sure the following dependencies are installed on your Windows machine.

| Dependency | How to Check | Download Link |
|------------|-------------|---------------|
| **Git** | `git --version` in terminal | [git-scm.com](https://git-scm.com/download/win) |
| **.NET 10 SDK** | `dotnet --version` (should print `10.x.x`) | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| **WebView2 Runtime** | Pre-installed on Windows 11; check "Apps > Installed apps" for "Microsoft Edge WebView2 Runtime" | [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) |

> **Note:** You do **not** need Visual Studio. The project can be built entirely with the `dotnet` CLI that comes with the .NET SDK.

### Quick Start

```bash
# 1. Clone the repository
git clone https://github.com/1998moye/QMark.git
cd QMark

# 2. Restore NuGet dependencies (Markdig, WebView2, etc.)
dotnet restore

# 3. Build Release version
dotnet build -c Release

# 4. Run
dotnet run
```

After a successful build, the executable is located at:

```
bin/Release/net10.0-windows/MarkdownEditor.exe
```

### Tech Stack

- **Framework**: C# / WPF (.NET 10)
- **Preview Engine**: Microsoft WebView2
- **Markdown Parsing**: Markdig
- **Build Tool**: .NET SDK CLI (`dotnet`)

### File Association (Optional)

To register `.md` file association so you can double-click Markdown files to open them with QMark:

1. Right-click `MarkdownEditor.exe`
2. Select **"Run as administrator"**
3. The app will automatically register itself as the default handler for `.md` files

---

## 中文

### 功能特性

- **实时预览** — 基于 WebView2 的即时 Markdown 渲染
- **分栏模式** — 源码/预览并排显示，也支持纯源码或纯预览模式
- **三套主题** — 浅色、深色、护眼 Sepia 三种主题，切换流畅
- **文件树与大纲** — 内置侧边栏，包含文件浏览和自动生成的大纲目录
- **自由调整窗口大小** — 拖拽任意边缘或四角自由缩放窗口
- **粘贴保留 Markdown 格式** — 从网页（如 ChatGPT）复制内容粘贴时保留 Markdown 格式
- **文件关联** — 双击 `.md` 文件直接用 QMark 打开
- **最近文件** — 启动时快速访问最近打开的文件

### 前置依赖

编译前请确保 Windows 电脑上已安装以下依赖。

| 依赖项 | 检查方式 | 下载地址 |
|--------|---------|---------|
| **Git** | 终端执行 `git --version` | [git-scm.com](https://git-scm.com/download/win) |
| **.NET 10 SDK** | 终端执行 `dotnet --version`（应输出 `10.x.x`） | [dotnet.microsoft.com](https://dotnet.microsoft.com/download) |
| **WebView2 Runtime** | Windows 11 默认已安装；可在"设置 > 应用 > 已安装的应用"中搜索确认 | [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) |

> **注意：** 不需要安装 Visual Studio。项目完全可以通过 .NET SDK 自带的 `dotnet` 命令行工具编译和运行。

### 快速开始

```bash
# 1. 克隆仓库
git clone https://github.com/1998moye/QMark.git
cd QMark

# 2. 还原 NuGet 依赖（Markdig、WebView2 等）
dotnet restore

# 3. 编译 Release 版本
dotnet build -c Release

# 4. 运行
dotnet run
```

编译成功后，可执行文件位于：

```
bin/Release/net10.0-windows/MarkdownEditor.exe
```

### 技术栈

- **框架**: C# / WPF (.NET 10)
- **预览引擎**: Microsoft WebView2
- **Markdown 解析**: Markdig
- **构建工具**: .NET SDK CLI (`dotnet`)

### 文件关联（可选）

如需将 QMark 注册为 `.md` 文件的默认打开程序：

1. 右键 `MarkdownEditor.exe`
2. 选择**"以管理员身份运行"**
3. 程序会自动注册 `.md` 文件关联

---

*QMark — Write Markdown, beautifully.*  
*QMark — 优雅地书写 Markdown。*
