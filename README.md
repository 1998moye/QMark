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

### System Requirements

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

### Quick Start

```bash
# Clone the repository
git clone https://github.com/1998moye/QMark.git
cd QMark

# Restore dependencies
dotnet restore

# Build
dotnet build -c Release

# Run
dotnet run
```

The executable will be at `bin/Release/net10.0-windows/MarkdownEditor.exe`.

### Tech Stack

- **Framework**: C# / WPF (.NET 10)
- **Preview Engine**: Microsoft WebView2
- **Markdown Parsing**: Markdig

### File Association (Optional)

To register `.md` file association, right-click the `MarkdownEditor.exe` and choose "Run as administrator". The app will register itself as the default handler for `.md` files.

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

### 系统要求

- Windows 10/11
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

### 快速开始

```bash
# 克隆仓库
git clone https://github.com/1998moye/QMark.git
cd QMark

# 还原依赖
dotnet restore

# 编译
dotnet build -c Release

# 运行
dotnet run
```

可执行文件位于 `bin/Release/net10.0-windows/MarkdownEditor.exe`。

### 技术栈

- **框架**: C# / WPF (.NET 10)
- **预览引擎**: Microsoft WebView2
- **Markdown 解析**: Markdig

### 文件关联（可选）

右键 `MarkdownEditor.exe` 选择"以管理员身份运行"，程序会自动注册为 `.md` 文件的默认打开方式。

---

*QMark — Write Markdown, beautifully.*  
*QMark — 优雅地书写 Markdown。*
