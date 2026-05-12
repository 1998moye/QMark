using Markdig;
using Microsoft.Web.WebView2.Core;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace MarkdownEditor;

public partial class MainWindow : Window
{
    public static RoutedCommand BoldCommand = new();
    public static RoutedCommand ItalicCommand = new();

    private readonly MarkdownPipeline _pipeline;
    private readonly System.Timers.Timer _debounceTimer;
    private string? _currentFilePath;
    private bool _isDirty;
    private bool _clickToEditEnabled = true;
    private string _editorText = "";
    private bool _canUndo;
    private bool _canRedo;

    private enum ViewMode { Source, Split, Preview }
    private ViewMode _currentViewMode = ViewMode.Preview;

    private bool _sidebarOpen;
    private bool _btnDragging;
    private bool _sidebarBtnPinnedLeft = true;
    private bool _sidebarBtnHoverExpanded = true;
    private bool _sidebarBtnAutoHideEnabled;
    private bool _sidebarBtnInitialized;
    private double _dragMouseOffX, _dragMouseOffY;
    private double _btnOffX, _btnOffY;
    private double _dragCheckX, _dragCheckY;
    private string _sidebarTab = "outline"; // "outline" or "files"

    // Floating overlay window (transparent owned Window on top of WebView2)
    private Window? _floatOverlayWin;
    private Window? _resizeOverlayWin;
    private Canvas? _resizeOverlayLayer;
    private Canvas? SidebarFloatLayer;   // replaces XAML-generated field
    private Border? SidebarFloatBtn;     // replaces XAML-generated field
    private TextBlock? SidebarFloatIcon; // replaces XAML-generated field
    private string _currentThemeId = "light";
    private ThemeColors? _currentColors;
    private List<string> _enabledThemeIds = new() { "light", "dark", "sepia" };
    private List<OutlineHeading> _outlineHeadings = new();

    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QMark", "settings.json");

    private static readonly string RecentFilesPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QMark", "recent.json");

    private record RecentFileEntry(string Path, DateTime OpenedAt);

    private record AppSettings(bool ClickToEditEnabled, string CurrentThemeId, List<string> EnabledThemeIds, string? LastFilePath = null);

    private record OutlineHeading(int Index, int Level, string Text, int LineIndex);

    // Win32 imports for custom borderless resize and maximize bounds
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int RGN_OR = 2;
    private const double ResizeBorderDip = 2;
    private const double ResizeCornerDip = 12;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // Win32 imports for overlay window: prevent focus steal
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);

    // Win32 imports for clipping overlay window to button shape (SetWindowRgn)
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDest, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left, top, right, bottom;
    }

    private static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                // Deserialize with fallback for old format (int ThemeIndex)
                try
                {
                    return JsonSerializer.Deserialize<AppSettings>(json)
                           ?? new AppSettings(true, "light", new() { "light", "dark", "sepia" });
                }
                catch
                {
                    // Old format: try as legacy settings with ThemeIndex
                    var legacy = JsonSerializer.Deserialize<LegacySettings>(json);
                    if (legacy != null)
                    {
                        var id = legacy.ThemeIndex < AllThemes.Count ? AllThemes[legacy.ThemeIndex].Id : "light";
                        return new AppSettings(legacy.ClickToEditEnabled, id, new() { "light", "dark", "sepia" });
                    }
                }
            }
        }
        catch { }
        return new AppSettings(true, "light", new() { "light", "dark", "sepia" });
    }

    private record LegacySettings(bool ClickToEditEnabled, int ThemeIndex);

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var settings = new AppSettings(
                ClickToEditToggle.IsChecked == true,
                _currentThemeId,
                _enabledThemeIds,
                _currentFilePath
            );
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch { }
    }

    private void SaveLastFilePath(string? filePath)
    {
        try
        {
            // Read current settings, update only LastFilePath field
            var json = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath) : "{}";
            var settings = JsonSerializer.Deserialize<AppSettings>(json)
                           ?? new AppSettings(true, "light", new() { "light", "dark", "sepia" });
            var updated = settings with { LastFilePath = filePath };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(updated));
        }
        catch { }
    }

    #region Theme System

    private record ThemeColors(
        string Bg, string Text, string Border, string Link,
        string BlockText, string BlockBorder, string CodeBg, string PreBg,
        string ThBg, string AltRowBg, string ScrollThumb, string ScrollHover
    );

    private record ThemeDef(string Id, string Name, ThemeColors Colors, string EditorBg, string EditorFg);

    private static string BuildCss(ThemeColors c) => $$"""
        * { margin: 0; padding: 0; box-sizing: border-box; }
        html { overflow-x: hidden; }
        body {
            font-family: -apple-system, "Segoe UI", "Noto Sans SC", sans-serif;
            padding: 30px 120px; line-height: 1.7;
            color: {{c.Text}}; background: {{c.Bg}};
            max-width: 100%; margin: 0 auto;
            word-wrap: break-word; overflow-wrap: break-word;
            overflow-x: hidden;
        }
        h1, h2 { border-bottom: 1px solid {{c.Border}}; }
        a { color: {{c.Link}}; text-decoration: none; }
        a:hover { text-decoration: underline; }
        ul, ol { padding-left: 2em; margin-bottom: 1em; }
        li { margin: 0.25em 0; }
        blockquote { margin: 0 0 1em 0; padding: 0 1em; color: {{c.BlockText}}; border-left: 4px solid {{c.BlockBorder}}; }
        code { font-family: "Cascadia Code", "JetBrains Mono", Consolas, monospace; background: {{c.CodeBg}}; padding: 0.2em 0.4em; border-radius: 4px; font-size: 85%; }
        pre { background: {{c.PreBg}}; border-radius: 8px; padding: 16px; overflow-x: auto; margin-bottom: 1em; overflow-y: hidden; }
        pre code { background: none; padding: 0; font-size: 100%; }
        table { border-collapse: collapse; width: 100%; margin-bottom: 1em; }
        th, td { border: 1px solid {{c.Border}}; padding: 10px 14px; text-align: left; }
        th { background: {{c.ThBg}}; font-weight: 600; }
        tr:nth-child(even) { background: {{c.AltRowBg}}; }
        img { max-width: 100%; height: auto; border-radius: 6px; }
        hr { border: 0; border-top: 1px solid {{c.Border}}; margin: 2em 0; }
        ::-webkit-scrollbar { width: 8px; }
        ::-webkit-scrollbar-track { background: transparent; }
        ::-webkit-scrollbar-thumb { background: {{c.ScrollThumb}}; border-radius: 4px; }
        ::-webkit-scrollbar-thumb:hover { background: {{c.ScrollHover}}; }
        #qmark-back-to-top {
            position: fixed;
            bottom: 24px; right: 24px;
            width: 44px; height: 44px;
            border-radius: 50%;
            border: 1px solid {{c.Border}};
            background: {{c.Bg}};
            color: {{c.Text}};
            cursor: pointer;
            display: flex; align-items: center; justify-content: center;
            font-size: 18px;
            line-height: 1;
            opacity: 0;
            visibility: hidden;
            transition: opacity 0.25s ease, visibility 0.25s ease, transform 0.15s ease;
            box-shadow: 0 2px 8px rgba(0,0,0,0.12);
            user-select: none;
            -webkit-user-select: none;
            z-index: 9999;
        }
        #qmark-back-to-top.visible { opacity: 1; visibility: visible; }
        #qmark-back-to-top:hover { transform: scale(1.08); }
        """;

    private static readonly List<ThemeDef> AllThemes = new()
    {
        new("light", "简约白", new("#ffffff", "#333333", "#e2e8f0", "#2563eb",
            "#6a737d", "#2563eb", "#f5f5f5", "#f5f5f5", "#f8fafc", "#fafbfc", "#d0d0d0", "#b0b0b0"),
            "#ffffff", "#333333"),
        new("dark", "深邃黑", new("#1a1b26", "#e2e8f0", "#2d3748", "#60a5fa",
            "#94a3b8", "#3b82f6", "#2d3748", "#2d3748", "#1e293b", "#1a1b26", "#4a5568", "#718096"),
            "#1a1b26", "#e2e8f0"),
        new("sepia", "护眼黄", new("#fbf7f0", "#5b4636", "#e8dcc8", "#8b5e3c",
            "#8b7d6b", "#c4a882", "#f5f0e8", "#f5f0e8", "#f5f0e8", "#f8f4ec", "#d5c8b8", "#c0b0a0"),
            "#fbf7f0", "#5b4636"),
        new("nord", "极光蓝", new("#eceff4", "#2e3440", "#d8dee9", "#5e81ac",
            "#616e88", "#5e81ac", "#e5e9f0", "#e5e9f0", "#e5e9f0", "#f0f2f7", "#b0b8c8", "#8f98a8"),
            "#eceff4", "#2e3440"),
        new("dracula", "暗夜紫", new("#282a36", "#f8f8f2", "#44475a", "#bd93f9",
            "#6272a4", "#bd93f9", "#44475a", "#44475a", "#21222c", "#2c2d3a", "#6272a4", "#7d8db8"),
            "#282a36", "#f8f8f2"),
        new("graphite", "石墨灰", new("#2d2d2d", "#d4d4d4", "#404040", "#569cd6",
            "#9a9a9a", "#569cd6", "#1e1e1e", "#1e1e1e", "#333333", "#2a2a2a", "#555555", "#666666"),
            "#2d2d2d", "#d4d4d4"),
        new("solarized", "暖阳橙", new("#fdf6e3", "#657b83", "#eee8d5", "#268bd2",
            "#93a1a1", "#268bd2", "#eee8d5", "#eee8d5", "#f5efdc", "#faf4e8", "#d0c8a8", "#b8b090"),
            "#fdf6e3", "#657b83"),
        new("mint", "薄荷绿", new("#e8f5e9", "#2e7d32", "#c8e6c9", "#1b5e20",
            "#4caf50", "#2e7d32", "#dcedc8", "#dcedc8", "#d0e8d0", "#eef8ee", "#a5d6a7", "#81c784"),
            "#e8f5e9", "#2e7d32"),
        new("sakura", "樱花粉", new("#fff0f5", "#6b2142", "#f8d7e0", "#d63384",
            "#ad5a7a", "#d63384", "#fce4ec", "#fce4ec", "#f8d7e0", "#fef0f5", "#f0b3c8", "#e890a8"),
            "#fff0f5", "#6b2142"),
        new("moonlight", "月白", new("#f0f4ff", "#2c3e50", "#d4e0f0", "#3b82f6",
            "#5a6a7a", "#3b82f6", "#e8edf6", "#e8edf6", "#e4ebf5", "#f5f8ff", "#c0cce0", "#a0b0cc"),
            "#f0f4ff", "#2c3e50"),
        new("ice", "酷冰蓝", new("#e3f2fd", "#1565c0", "#bbdefb", "#0d47a1",
            "#1976d2", "#0d47a1", "#d4e8f8", "#d4e8f8", "#d0e4f4", "#ecf4fc", "#90caf9", "#64b5f6"),
            "#e3f2fd", "#1565c0"),
        new("latte", "拿铁棕", new("#f5ebe0", "#4e342e", "#d7ccc8", "#6d4c41",
            "#8d6e63", "#6d4c41", "#efe3d8", "#efe3d8", "#e8dbd0", "#f2eae0", "#c8b8a8", "#b0a090"),
            "#f5ebe0", "#4e342e"),
        new("retro", "复古绿", new("#0a0a0a", "#33ff33", "#1a3a1a", "#66ff66",
            "#33cc33", "#33ff33", "#0d1a0d", "#0d1a0d", "#0f2a0f", "#0d200d", "#1a4a1a", "#2a6a2a"),
            "#0a0a0a", "#33ff33"),
        new("highcontrast", "高对比", new("#000000", "#ffffff", "#666666", "#ffff00",
            "#cccccc", "#ffff00", "#1a1a1a", "#1a1a1a", "#222222", "#111111", "#555555", "#888888"),
            "#000000", "#ffffff"),
    };

    #endregion

    public MainWindow()
    {
        InitializeComponent();

        // Load persisted settings
        var saved = LoadSettings();
        _clickToEditEnabled = saved.ClickToEditEnabled;
        _currentThemeId = saved.CurrentThemeId;
        _enabledThemeIds = saved.EnabledThemeIds ?? new() { "light", "dark", "sepia" };
        var pendingLastFile = saved.LastFilePath;  // capture before SaveSettings() overwrites it
        ModePreview.IsChecked = true;
        ClickToEditToggle.IsChecked = saved.ClickToEditEnabled;

        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

        _debounceTimer = new System.Timers.Timer(300) { AutoReset = false };
        _debounceTimer.Elapsed += (_, _) => Dispatcher.Invoke(UpdatePreview);

        // Apply proper rounded corner clip (ClipToBounds doesn't respect CornerRadius)
        ChromeBorder.SizeChanged += (s, e) =>
        {
            var radius = WindowState == WindowState.Maximized ? 0 : 10;
            ChromeBorder.Clip = new RectangleGeometry(
                new Rect(0, 0, ChromeBorder.ActualWidth, ChromeBorder.ActualHeight), radius, radius);
        };

        _editorText = GetWelcomeContent();
        PopulateThemeSelector();
        RegisterFileAssociations();
        InitializeWebView();
        UpdateViewMode();
        UpdateTitle();
        UpdateStatusBar();

        // Position floating sidebar button (transparent overlay Window, above WebView2)
        Loaded += (_, _) =>
        {
            InitResizeOverlay();
            RefreshResizeSurface();
            InitFloatOverlay();
            RefreshSidebarSurface();
            LocationChanged += (_, _) =>
            {
                RefreshResizeSurface();
                RefreshSidebarSurface();
            };
        };
        SizeChanged += (_, _) =>
        {
            RefreshResizeSurface();
            RefreshSidebarSurface();
        };
        ContentAreaGrid.SizeChanged += (_, _) => RefreshSidebarSurface();

        // Handle command-line argument: file opened via .md association
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            var filePath = args[1];
            // Defer to ensure WebView is initialized before loading
            Dispatcher.BeginInvoke(new Action(() => OpenRecentFile(filePath)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        // Restore last opened file when launched without a file argument
        else if (!string.IsNullOrEmpty(pendingLastFile) && File.Exists(pendingLastFile))
        {
            var lastFile = pendingLastFile;
            Dispatcher.BeginInvoke(new Action(() => OpenRecentFile(lastFile)),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private static void RegisterFileAssociations()
    {
        try
        {
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exePath == null) return;
            var cmdTemplate = $"\"{exePath}\" \"%1\"";
            var appName = "QMark Markdown Editor";
            var progId = "QMark.md";

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\.md");
            key.SetValue("", progId);

            using var progKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}");
            progKey.SetValue("", appName);

            // Use md.ico for .md file association icon
            var exeDir = Path.GetDirectoryName(exePath);
            var mdIcoPath = exeDir != null ? Path.Combine(exeDir, "icons", "md.ico") : null;
            var iconPath = mdIcoPath ?? $"{exePath},0";
            using var iconKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\DefaultIcon");
            iconKey.SetValue("", iconPath);

            using var cmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{progId}\shell\open\command");
            cmdKey.SetValue("", cmdTemplate);

            using var appKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\MarkdownEditor.exe");
            using var appCmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\MarkdownEditor.exe\shell\open\command");
            appCmdKey.SetValue("", cmdTemplate);
            using var appNameKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\MarkdownEditor.exe\FriendlyAppName");
            appNameKey.SetValue("", appName);

            // Also set DefaultIcon for the application registration
            using var appIconKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\MarkdownEditor.exe\DefaultIcon");
            appIconKey.SetValue("", iconPath);
        }
        catch { /* 注册失败不影响正常使用 */ }
    }

    private static string GetWelcomeContent()
    {
        return """
            # 欢迎使用 QMark

            QMark 是一款简洁优雅的 Markdown 编辑器。

            ## 快速入门

            - **工具栏**：使用上方工具栏进行格式化和文件操作
            - **预览模式**：点击"预览"查看渲染效果，"分栏"可同时编辑和预览
            - **主题切换**：在主题下拉菜单中选择你喜欢的配色方案
            - **快捷键**：`Ctrl+O` 打开文件，`Ctrl+S` 保存，`Ctrl+Z` 撤销

            ## 功能示例

            **粗体** *斜体* ~~删除线~~ `行内代码`

            > 引用块示例 — 这是引用的内容

            ```csharp
            // 代码块示例
            Console.WriteLine("Hello QMark!");
            ```

            | 功能 | 快捷键 | 说明 |
            |------|--------|------|
            | 粗体 | Ctrl+B | 选中文本加粗 |
            | 斜体 | Ctrl+I | 选中文本斜体 |
            | 保存 | Ctrl+S | 保存当前文件 |

            - 无序列表项 1
            - 无序列表项 2

            1. 有序列表项 1
            2. 有序列表项 2
            """;
    }

    private string GetEditorHtml(string themeId)
    {
        var theme = AllThemes.Find(t => t.Id == themeId) ?? AllThemes[0];
        var bg = theme.EditorBg;
        var fg = theme.EditorFg;
        var thumb = theme.Colors.ScrollThumb;
        var hover = theme.Colors.ScrollHover;
        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <style>
                * { margin: 0; padding: 0; box-sizing: border-box; }
                html, body { height: 100%; overflow: hidden; }
                body { background: {{bg}}; }
                #editor {
                    width: 100%; height: 100%; border: none; outline: none; resize: none;
                    overflow-y: auto;
                    padding: 30px;
                    font-family: 'Cascadia Code', 'JetBrains Mono', Consolas, monospace;
                    font-size: 14px; line-height: 1.7;
                    color: {{fg}}; background: {{bg}};
                }
                @media (min-width: 800px) {
                    #editor { padding: 30px 180px 30px 120px; }
                }
                ::-webkit-scrollbar { width: 8px; }
                ::-webkit-scrollbar-track { background: transparent; }
                ::-webkit-scrollbar-thumb { background: {{thumb}}; border-radius: 4px; }
                ::-webkit-scrollbar-thumb:hover { background: {{hover}}; }
            </style>
            </head>
            <body>
            <textarea id="editor" spellcheck="false"></textarea>
            <script>
                var editor = document.getElementById('editor');
                if (window.__ic) { editor.value = window.__ic; }
                editor.addEventListener('input', function() {
                    try { window.chrome.webview.postMessage('c:' + this.value); } catch(e) {}
                });
                window.setContent = function(text) { editor.value = text; };
                window.getContent = function() { return editor.value; };
                window.wrapSelection = function(before, after) {
                    var start = editor.selectionStart, end = editor.selectionEnd;
                    var selected = editor.value.substring(start, end);
                    editor.value = editor.value.substring(0, start) + before + selected + after + editor.value.substring(end);
                    if (selected.length > 0) {
                        editor.selectionStart = start + before.length;
                        editor.selectionEnd = start + before.length + selected.length;
                    } else {
                        editor.selectionStart = start + before.length;
                        editor.selectionEnd = start + before.length;
                    }
                    editor.dispatchEvent(new Event('input'));
                };
                window.insertAtCursor = function(text) {
                    var start = editor.selectionStart, end = editor.selectionEnd;
                    editor.value = editor.value.substring(0, start) + text + editor.value.substring(end);
                    editor.selectionStart = editor.selectionEnd = start + text.length;
                    editor.dispatchEvent(new Event('input'));
                };
                window.insertAtLineStart = function(prefix) {
                    var pos = editor.selectionStart;
                    var text = editor.value;
                    var lineStart = text.lastIndexOf('\n', pos - 1) + 1;
                    if (lineStart < 0) lineStart = 0;
                    editor.value = text.substring(0, lineStart) + prefix + text.substring(lineStart);
                    editor.selectionStart = editor.selectionEnd = pos + prefix.length;
                    editor.dispatchEvent(new Event('input'));
                };

                // ----- HTML -> Markdown converter (paste from web pages like ChatGPT) -----
                window.htmlToMarkdown = function(html) {
                    var FENCE = '\u0060\u0060\u0060';
                    var doc;
                    try { doc = new DOMParser().parseFromString(html, 'text/html'); }
                    catch (e) { return ''; }
                    var root = doc && doc.body ? doc.body : null;
                    if (!root) return '';

                    function escapeMd(s) {
                        return String(s)
                            .replace(/\u00a0/g, ' ')
                            .replace(/\\/g, '\\\\')
                            .replace(/([*_`\[\]<>])/g, '\\$1');
                    }
                    function collapseWs(s) {
                        return String(s).replace(/\s+/g, ' ');
                    }
                    function inline(node) {
                        if (!node) return '';
                        if (node.nodeType === 3) return collapseWs(escapeMd(node.nodeValue));
                        if (node.nodeType !== 1) return '';
                        var tag = node.tagName.toLowerCase();
                        var inner = childrenInline(node);
                        switch (tag) {
                            case 'br': return '  \n';
                            case 'strong':
                            case 'b': {
                                var t = inner.trim();
                                return t ? '**' + t + '**' : '';
                            }
                            case 'em':
                            case 'i': {
                                var t2 = inner.trim();
                                return t2 ? '*' + t2 + '*' : '';
                            }
                            case 'del':
                            case 's':
                            case 'strike': {
                                var t3 = inner.trim();
                                return t3 ? '~~' + t3 + '~~' : '';
                            }
                            case 'code': {
                                var raw = (node.textContent || '').replace(/\u00a0/g, ' ');
                                return '`' + raw + '`';
                            }
                            case 'a': {
                                var href = node.getAttribute('href') || '';
                                var label = inner.trim() || href;
                                if (!href) return label;
                                return '[' + label + '](' + href + ')';
                            }
                            case 'img': {
                                var src = node.getAttribute('src') || '';
                                var alt = node.getAttribute('alt') || '';
                                return '![' + alt + '](' + src + ')';
                            }
                            case 'span':
                            case 'font':
                            case 'u':
                            case 'mark':
                            case 'small':
                            case 'sub':
                            case 'sup':
                                return inner;
                            default:
                                return inner;
                        }
                    }
                    function childrenInline(node) {
                        var out = '';
                        for (var i = 0; i < node.childNodes.length; i++) {
                            out += inline(node.childNodes[i]);
                        }
                        return out;
                    }
                    function inlineText(node) {
                        return childrenInline(node).replace(/[ \t]+\n/g, '\n').trim();
                    }
                    function block(node, indent) {
                        if (!node) return '';
                        if (node.nodeType === 3) {
                            var t = collapseWs(escapeMd(node.nodeValue)).trim();
                            return t ? t : '';
                        }
                        if (node.nodeType !== 1) return '';
                        var tag = node.tagName.toLowerCase();
                        switch (tag) {
                            case 'h1': case 'h2': case 'h3':
                            case 'h4': case 'h5': case 'h6': {
                                var level = parseInt(tag.substring(1), 10);
                                var hashes = '';
                                for (var i = 0; i < level; i++) hashes += '#';
                                return hashes + ' ' + inlineText(node);
                            }
                            case 'p':
                                return inlineText(node);
                            case 'br':
                                return '';
                            case 'hr':
                                return '---';
                            case 'pre': {
                                var codeEl = node.querySelector('code');
                                var lang = '';
                                if (codeEl) {
                                    var cls = codeEl.getAttribute('class') || '';
                                    var m = cls.match(/language-([\w-]+)/i);
                                    if (m) lang = m[1];
                                }
                                var raw = (codeEl ? codeEl.textContent : node.textContent) || '';
                                raw = raw.replace(/\u00a0/g, ' ').replace(/\r\n?/g, '\n');
                                if (raw.endsWith('\n')) raw = raw.substring(0, raw.length - 1);
                                return FENCE + lang + '\n' + raw + '\n' + FENCE;
                            }
                            case 'blockquote': {
                                var inner = blocks(node, indent);
                                return inner.split('\n').map(function(l) { return l.length ? '> ' + l : '>'; }).join('\n');
                            }
                            case 'ul':
                                return list(node, false, indent || '');
                            case 'ol':
                                return list(node, true, indent || '');
                            case 'table':
                                return table(node);
                            case 'div':
                            case 'section':
                            case 'article':
                            case 'header':
                            case 'footer':
                            case 'main':
                            case 'aside':
                                return blocks(node, indent);
                            default:
                                return inlineText(node);
                        }
                    }
                    function blocks(node, indent) {
                        var parts = [];
                        for (var i = 0; i < node.childNodes.length; i++) {
                            var c = node.childNodes[i];
                            var b = block(c, indent);
                            if (b !== '' && b != null) parts.push(b);
                        }
                        return parts.join('\n\n');
                    }
                    function list(node, ordered, indent) {
                        var items = [];
                        var n = 1;
                        for (var i = 0; i < node.children.length; i++) {
                            var li = node.children[i];
                            if (!li || li.tagName.toLowerCase() !== 'li') continue;

                            // Split direct child blocks (nested list etc.) from inline content
                            var inlineBuf = '';
                            var nestedBlocks = [];
                            for (var j = 0; j < li.childNodes.length; j++) {
                                var cn = li.childNodes[j];
                                if (cn.nodeType === 1) {
                                    var t = cn.tagName.toLowerCase();
                                    if (t === 'ul' || t === 'ol') {
                                        nestedBlocks.push(list(cn, t === 'ol', indent + '  '));
                                        continue;
                                    }
                                    if (t === 'p') {
                                        inlineBuf += childrenInline(cn);
                                        inlineBuf += '\n';
                                        continue;
                                    }
                                    if (t === 'pre') {
                                        nestedBlocks.push(block(cn, indent + '  '));
                                        continue;
                                    }
                                    inlineBuf += inline(cn);
                                } else if (cn.nodeType === 3) {
                                    inlineBuf += collapseWs(escapeMd(cn.nodeValue));
                                }
                            }
                            var marker = ordered ? (n + '. ') : '- ';
                            var lineHead = indent + marker;
                            var firstLine = inlineBuf.replace(/\s+$/g, '').split('\n')[0] || '';
                            var rest = inlineBuf.replace(/\s+$/g, '').split('\n').slice(1).join('\n');
                            var entry = lineHead + firstLine;
                            if (rest) {
                                entry += '\n' + rest.split('\n').map(function(l) {
                                    return indent + '  ' + l;
                                }).join('\n');
                            }
                            for (var k = 0; k < nestedBlocks.length; k++) {
                                entry += '\n' + nestedBlocks[k];
                            }
                            items.push(entry);
                            n++;
                        }
                        return items.join('\n');
                    }
                    function table(node) {
                        var rows = [];
                        var trs = node.querySelectorAll('tr');
                        for (var i = 0; i < trs.length; i++) {
                            var tr = trs[i];
                            var cells = [];
                            for (var j = 0; j < tr.children.length; j++) {
                                var td = tr.children[j];
                                var name = td.tagName.toLowerCase();
                                if (name !== 'td' && name !== 'th') continue;
                                var txt = childrenInline(td).replace(/\n+/g, ' ').replace(/\|/g, '\\|').trim();
                                cells.push(txt);
                            }
                            if (cells.length) rows.push(cells);
                        }
                        if (!rows.length) return '';
                        var header = rows[0];
                        var body = rows.slice(1);
                        var line = '| ' + header.join(' | ') + ' |';
                        var sep = '| ' + header.map(function() { return '---'; }).join(' | ') + ' |';
                        var bodyLines = body.map(function(r) {
                            while (r.length < header.length) r.push('');
                            return '| ' + r.join(' | ') + ' |';
                        });
                        return [line, sep].concat(bodyLines).join('\n');
                    }

                    var md = blocks(root, '');
                    md = md.replace(/\n{3,}/g, '\n\n').replace(/^\s+|\s+$/g, '');
                    return md;
                };

                // Intercept paste so web-copied content (HTML) becomes Markdown.
                editor.addEventListener('paste', function(e) {
                    var cd = e.clipboardData || window.clipboardData;
                    if (!cd) return;
                    var html = cd.getData('text/html');
                    var text;
                    if (html && html.trim().length > 0) {
                        try { text = window.htmlToMarkdown(html); } catch (err) { text = ''; }
                        if (!text || text.length === 0) text = cd.getData('text/plain');
                    } else {
                        text = cd.getData('text/plain');
                    }
                    if (text == null) return;
                    e.preventDefault();
                    window.insertAtCursor(text);
                });
            </script>
            </body>

            </html>
            """;
    }

    #region Window Controls

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            // Only start drag if not double-click (maximize)
            if (e.ClickCount == 2 && WindowState == WindowState.Normal)
                MaximizeWindow(sender, e);
            else if (e.ClickCount == 2)
                MinimizeWindow(sender, e);
            else
            {
                // 不在窗口边缘区域时才允许拖拽，否则交给 WM_NCHITTEST 处理 resize
                GetCursorPos(out var cursorPos);
                if (GetResizeHitTest(cursorPos.x, cursorPos.y) == 0)
                    DragMove();
            }
        }
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseWindow(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (WindowState == WindowState.Maximized) return;
        double newWidth = ActualWidth + e.HorizontalChange;
        double newHeight = ActualHeight + e.VerticalChange;
        if (newWidth > MinWidth) Width = newWidth;
        if (newHeight > MinHeight) Height = newHeight;
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        // Clear region before resize so edges are free during the transition
        ClearOverlayRegion();
        ClearResizeOverlayRegion();
        // Two-stage refresh: main window size updates first, then overlay canvas layout settles.
        Dispatcher.BeginInvoke(() =>
        {
            RefreshResizeSurface();
            SyncOverlayBounds();
            Dispatcher.BeginInvoke(() => RefreshSidebarSurface(),
                System.Windows.Threading.DispatcherPriority.Loaded);
            Dispatcher.BeginInvoke(() =>
            {
                RefreshResizeSurface();
                RefreshSidebarSurface();
            },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }, System.Windows.Threading.DispatcherPriority.Loaded);

        if (MaxIcon != null)
            MaxIcon.Text = WindowState == WindowState.Maximized ? "❐" : "□";

        if (BtnMaximize != null)
            BtnMaximize.ToolTip = WindowState == WindowState.Maximized ? "还原" : "最大化";

        // Remove corner radius when maximized to fill work area cleanly
        if (WindowState == WindowState.Maximized)
        {
            ChromeBorder.CornerRadius = new CornerRadius(0);
            ChromeBorder.BorderThickness = new Thickness(0);
            TitleBarBorder.CornerRadius = new CornerRadius(0);
            StatusBarBorder.CornerRadius = new CornerRadius(0);
            ChromeBorder.Clip = new RectangleGeometry(
                new Rect(0, 0, ChromeBorder.ActualWidth, ChromeBorder.ActualHeight), 0, 0);
        }
        else
        {
            ChromeBorder.CornerRadius = new CornerRadius(10);
            TitleBarBorder.CornerRadius = new CornerRadius(10, 10, 0, 0);
            StatusBarBorder.CornerRadius = new CornerRadius(0, 0, 10, 10);
            ChromeBorder.Clip = new RectangleGeometry(
                new Rect(0, 0, ChromeBorder.ActualWidth, ChromeBorder.ActualHeight), 10, 10);
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var hwndSource = HwndSource.FromHwnd(handle);
        hwndSource?.AddHook(WindowProc);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            if (WindowState == WindowState.Maximized)
            {
                handled = true;
                return (IntPtr)HTCLIENT;
            }

            var hit = GetResizeHitTestFromLParam(lParam);
            if (hit != 0)
            {
                handled = true;
                return (IntPtr)hit;
            }

            return IntPtr.Zero;
        }

        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var monitor = MonitorFromWindow(hwnd, 2); // MONITOR_DEFAULTTONEAREST
            if (monitor != IntPtr.Zero)
            {
                var monitorInfo = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var workArea = monitorInfo.rcWork;
                    var waWidth = workArea.right - workArea.left;
                    var waHeight = workArea.bottom - workArea.top;
                    // Maximize bounds
                    mmi.ptMaxPosition.x = workArea.left;
                    mmi.ptMaxPosition.y = workArea.top;
                    mmi.ptMaxSize.x = waWidth;
                    mmi.ptMaxSize.y = waHeight;
                    // Minimum track size is in physical pixels; WPF MinWidth/MinHeight are DIP.
                    mmi.ptMinTrackSize.x = DipToDevicePixelsX(MinWidth);
                    mmi.ptMinTrackSize.y = DipToDevicePixelsY(MinHeight);
                }
            }
            Marshal.StructureToPtr(mmi, lParam, false);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private IntPtr ResizeOverlayWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            if (WindowState == WindowState.Maximized)
                return IntPtr.Zero;

            var hit = GetResizeHitTestFromLParam(lParam);
            if (hit != 0)
            {
                handled = true;
                return (IntPtr)hit;
            }
        }
        else if (msg == WM_NCLBUTTONDOWN)
        {
            var hit = wParam.ToInt32();
            if (IsResizeHit(hit))
            {
                BeginMainWindowResize(hit, lParam);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private int GetResizeHitTestFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xffff));
        var y = unchecked((short)((value >> 16) & 0xffff));
        return GetResizeHitTest(x, y);
    }

    private int GetResizeHitTest(int screenX, int screenY)
    {
        if (WindowState == WindowState.Maximized)
            return 0;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
            return 0;

        var border = GetResizeBorderPixels();
        var insideWindow = screenX >= rect.left && screenX <= rect.right
                           && screenY >= rect.top && screenY <= rect.bottom;
        if (!insideWindow)
            return 0;

        var onLeft = screenX <= rect.left + border;
        var onRight = screenX >= rect.right - border;
        var onTop = screenY <= rect.top + border;
        var onBottom = screenY >= rect.bottom - border;

        if (onTop && onLeft) return HTTOPLEFT;
        if (onTop && onRight) return HTTOPRIGHT;
        if (onBottom && onLeft) return HTBOTTOMLEFT;
        if (onBottom && onRight) return HTBOTTOMRIGHT;
        if (onLeft) return HTLEFT;
        if (onRight) return HTRIGHT;
        if (onTop) return HTTOP;
        if (onBottom) return HTBOTTOM;
        return 0;
    }

    private int GetResizeBorderPixels()
    {
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return Math.Max(2, (int)Math.Ceiling(ResizeBorderDip * Math.Max(scaleX, scaleY)));
    }

    private int DipToDevicePixelsX(double value)
    {
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return Math.Max(1, (int)Math.Ceiling(value * scaleX));
    }

    private int DipToDevicePixelsY(double value)
    {
        var source = PresentationSource.FromVisual(this);
        var scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return Math.Max(1, (int)Math.Ceiling(value * scaleY));
    }

    private static bool IsResizeHit(int hit)
    {
        return hit == HTLEFT || hit == HTRIGHT || hit == HTTOP || hit == HTBOTTOM
               || hit == HTTOPLEFT || hit == HTTOPRIGHT || hit == HTBOTTOMLEFT || hit == HTBOTTOMRIGHT;
    }

    private void BeginMainWindowResize(int hit, IntPtr lParam)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || WindowState == WindowState.Maximized)
            return;

        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)hit, lParam == IntPtr.Zero ? GetCursorLParam() : lParam);
    }

    private static IntPtr GetCursorLParam()
    {
        GetCursorPos(out var pos);
        var value = ((pos.y & 0xffff) << 16) | (pos.x & 0xffff);
        return (IntPtr)value;
    }

    private static Cursor GetResizeCursor(int hit)
    {
        return hit switch
        {
            HTLEFT or HTRIGHT => Cursors.SizeWE,
            HTTOP or HTBOTTOM => Cursors.SizeNS,
            HTTOPLEFT or HTBOTTOMRIGHT => Cursors.SizeNWSE,
            HTTOPRIGHT or HTBOTTOMLEFT => Cursors.SizeNESW,
            _ => Cursors.Arrow
        };
    }

    private Border CreateResizeHotZone(int hit)
    {
        var zone = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            Cursor = GetResizeCursor(hit),
            Tag = hit
        };
        zone.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            BeginMainWindowResize(hit, IntPtr.Zero);
            e.Handled = true;
        };
        return zone;
    }

    private void AddResizeHotZones(Canvas layer)
    {
        layer.Children.Add(CreateResizeHotZone(HTLEFT));
        layer.Children.Add(CreateResizeHotZone(HTRIGHT));
        layer.Children.Add(CreateResizeHotZone(HTTOP));
        layer.Children.Add(CreateResizeHotZone(HTBOTTOM));
        layer.Children.Add(CreateResizeHotZone(HTTOPLEFT));
        layer.Children.Add(CreateResizeHotZone(HTTOPRIGHT));
        layer.Children.Add(CreateResizeHotZone(HTBOTTOMLEFT));
        layer.Children.Add(CreateResizeHotZone(HTBOTTOMRIGHT));
        PositionResizeHotZones(layer);
        layer.SizeChanged += (_, _) => PositionResizeHotZones(layer);
    }

    private void PositionResizeHotZones(Canvas layer)
    {
        if (layer.ActualWidth <= 0 || layer.ActualHeight <= 0)
            return;

        const double border = ResizeBorderDip;
        const double corner = ResizeCornerDip;
        var width = layer.ActualWidth;
        var height = layer.ActualHeight;

        foreach (var child in layer.Children.OfType<Border>())
        {
            if (child.Tag is not int hit || !IsResizeHit(hit))
                continue;

            switch (hit)
            {
                case HTLEFT:
                    SetHotZoneBounds(child, 0, corner, border, Math.Max(0, height - corner * 2));
                    break;
                case HTRIGHT:
                    SetHotZoneBounds(child, Math.Max(0, width - border), corner, border, Math.Max(0, height - corner * 2));
                    break;
                case HTTOP:
                    SetHotZoneBounds(child, corner, 0, Math.Max(0, width - corner * 2), border);
                    break;
                case HTBOTTOM:
                    SetHotZoneBounds(child, corner, Math.Max(0, height - border), Math.Max(0, width - corner * 2), border);
                    break;
                case HTTOPLEFT:
                    SetHotZoneBounds(child, 0, 0, corner, corner);
                    break;
                case HTTOPRIGHT:
                    SetHotZoneBounds(child, Math.Max(0, width - corner), 0, corner, corner);
                    break;
                case HTBOTTOMLEFT:
                    SetHotZoneBounds(child, 0, Math.Max(0, height - corner), corner, corner);
                    break;
                case HTBOTTOMRIGHT:
                    SetHotZoneBounds(child, Math.Max(0, width - corner), Math.Max(0, height - corner), corner, corner);
                    break;
            }
        }
    }

    private static void SetHotZoneBounds(FrameworkElement element, double left, double top, double width, double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        element.Height = height;
    }

    private void InitResizeOverlay()
    {
        if (_resizeOverlayWin != null)
            return;

        _resizeOverlayLayer = new Canvas { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        AddResizeHotZones(_resizeOverlayLayer);
        _resizeOverlayWin = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Owner = this,
            Content = _resizeOverlayLayer,
            Left = Left,
            Top = Top,
            Width = Math.Max(1, ActualWidth),
            Height = Math.Max(1, ActualHeight),
        };

        _resizeOverlayWin.SourceInitialized += (_, _) =>
        {
            var helper = new WindowInteropHelper(_resizeOverlayWin);
            var ex = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(helper.Handle)?.AddHook(ResizeOverlayWindowProc);
            UpdateResizeOverlayRegion();
        };

        _resizeOverlayWin.Show();
    }

    private void RefreshResizeSurface()
    {
        if (_resizeOverlayWin == null)
            return;

        if (WindowState == WindowState.Maximized)
        {
            ClearResizeOverlayRegion();
            _resizeOverlayWin.Hide();
            return;
        }

        if (!_resizeOverlayWin.IsVisible)
            _resizeOverlayWin.Show();

        SyncResizeOverlayBounds();
        UpdateResizeOverlayRegion();
    }

    private void SyncResizeOverlayBounds()
    {
        if (_resizeOverlayWin == null)
            return;

        _resizeOverlayWin.Left = Left;
        _resizeOverlayWin.Top = Top;
        _resizeOverlayWin.Width = Math.Max(1, ActualWidth);
        _resizeOverlayWin.Height = Math.Max(1, ActualHeight);
        if (_resizeOverlayLayer != null)
            PositionResizeHotZones(_resizeOverlayLayer);
    }

    private void UpdateResizeOverlayRegion()
    {
        if (_resizeOverlayWin == null)
            return;

        try
        {
            var helper = new WindowInteropHelper(_resizeOverlayWin);
            if (helper.Handle == IntPtr.Zero)
                return;

            var width = DipToDevicePixelsX(Math.Max(1, ActualWidth));
            var height = DipToDevicePixelsY(Math.Max(1, ActualHeight));
            var border = GetResizeBorderPixels();

            if (WindowState == WindowState.Maximized || width <= 0 || height <= 0)
            {
                var empty = CreateRectRgn(0, 0, 0, 0);
                SetWindowRgn(helper.Handle, empty, true);
                return;
            }

            var region = CreateRectRgn(0, 0, width, border);
            var bottom = CreateRectRgn(0, Math.Max(0, height - border), width, height);
            var left = CreateRectRgn(0, 0, border, height);
            var right = CreateRectRgn(Math.Max(0, width - border), 0, width, height);

            CombineRgn(region, region, bottom, RGN_OR);
            CombineRgn(region, region, left, RGN_OR);
            CombineRgn(region, region, right, RGN_OR);

            DeleteObject(bottom);
            DeleteObject(left);
            DeleteObject(right);

            SetWindowRgn(helper.Handle, region, true);
        }
        catch { }
    }

    private void ClearResizeOverlayRegion()
    {
        if (_resizeOverlayWin == null)
            return;

        try
        {
            var helper = new WindowInteropHelper(_resizeOverlayWin);
            if (helper.Handle != IntPtr.Zero)
                SetWindowRgn(helper.Handle, IntPtr.Zero, true);
        }
        catch { }
    }

    /// <summary>Keep the overlay fully transparent and let only the actual button element receive input.
    /// Avoid Win32 region clipping here — it has proven fragile and can hide the button entirely.</summary>
    private void UpdateOverlayRegion()
    {
        if (_floatOverlayWin == null) return;
        try
        {
            var helper = new WindowInteropHelper(_floatOverlayWin);
            if (helper.Handle != IntPtr.Zero)
                SetWindowRgn(helper.Handle, IntPtr.Zero, true);
        }
        catch { }
    }

    private void ClearOverlayRegion()
    {
        if (_floatOverlayWin == null) return;
        try
        {
            var helper = new WindowInteropHelper(_floatOverlayWin);
            if (helper.Handle != IntPtr.Zero)
                SetWindowRgn(helper.Handle, IntPtr.Zero, true);
        }
        catch { }
    }

    #endregion

    #region WebView2

    private async void InitializeWebView()
    {
        try
        {
            // Use dedicated user data folder to avoid conflicts with other processes
            var webViewDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "QMark", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, webViewDir, null);

            await PreviewWebView.EnsureCoreWebView2Async(env);
            PreviewWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            PreviewWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            PreviewWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            PreviewWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;

            // Listen for click messages and scroll sync from preview content
            PreviewWebView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == null) return;

                if (msg == "back-to-top")
                {
                    _ = Dispatcher.BeginInvoke(async () =>
                    {
                        _lastProgrammaticScrollTicks = DateTime.Now.Ticks;
                        _suppressScrollRef++;
                        try
                        {
                            _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(
                                "window.__progScroll=true;window.scrollTo({top:0,behavior:'smooth'});setTimeout(function(){window.__progScroll=false;},600);");
                            _ = EditorWebView.CoreWebView2?.ExecuteScriptAsync(
                                "window.__progScroll=true;var e=document.getElementById('editor');e.scrollTop=0;setTimeout(function(){window.__progScroll=false;},600);");
                            await Task.Delay(500);
                        }
                        finally { _suppressScrollRef--; }
                    });
                }
                else if (msg == "preview-click" && _clickToEditEnabled)
                {
                    Dispatcher.Invoke(async () =>
                    {
                        if (_currentViewMode == ViewMode.Preview)
                        {
                            ModeSplit.IsChecked = true;
                            await EditorWebView.ExecuteScriptAsync("document.getElementById('editor').focus()");
                            StatusText.Text = "已切换到分栏模式（点击预览进入编辑）";
                        }
                    });
                }
                else if (msg.StartsWith("ps:"))
                {
                    // Preview scroll sync
                    if (double.TryParse(msg[3..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            // 如果距离上次程序化滚动不到 1200ms，忽略此消息
                            if ((DateTime.Now.Ticks - _lastProgrammaticScrollTicks) / TimeSpan.TicksPerMillisecond < 1200)
                                return;
                            if (_suppressScrollRef > 0) return;
                            _suppressScrollRef++;
                            try
                            {
                                // 同步设置 __progScroll 再执行滚动，防止滚动触发的新消息被发出去
                                _ = EditorWebView.CoreWebView2?.ExecuteScriptAsync(
                                    $"window.__progScroll=true;var e=document.getElementById('editor');e.scrollTop=(e.scrollHeight-e.clientHeight)*{pct.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)};setTimeout(function(){{window.__progScroll=false;}},150);");
                                // 延迟清除 suppress，确保对方的防抖 setTimeout 执行时仍被抑制
                                await Task.Delay(200);
                            }
                            finally { _suppressScrollRef--; }
                        });
                }
            };

            // Inject click-detection and scroll-sync scripts on every page
            await PreviewWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                document.addEventListener('click', function() {
                    try { window.chrome.webview.postMessage('preview-click'); } catch(e) {}
                });
                document.addEventListener('scroll', function() {
                    if (window.__progScroll) return;
                    clearTimeout(window._pst);
                    window._pst = setTimeout(function() {
                        if (window.__progScroll) return;
                        var m = document.documentElement.scrollHeight - document.documentElement.clientHeight;
                        try { window.chrome.webview.postMessage('ps:' + (m > 0 ? document.documentElement.scrollTop / m : 0)); } catch(e) {}
                    }, 50);
                });
            ");

            // Initialize editor WebView2
            await EditorWebView.EnsureCoreWebView2Async(env);
            EditorWebView.CoreWebView2.Settings.IsScriptEnabled = true;
            EditorWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            EditorWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            EditorWebView.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            EditorWebView.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;

            // Listen for content changes and scroll sync from editor
            EditorWebView.CoreWebView2.WebMessageReceived += (_, args) =>
            {
                var msg = args.TryGetWebMessageAsString();
                if (msg == null) return;

                if (msg.StartsWith("s:"))
                {
                    // Editor scroll sync
                    if (double.TryParse(msg[2..], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pct))
                        _ = Dispatcher.BeginInvoke(async () =>
                        {
                            // 如果距离上次程序化滚动不到 1200ms，忽略此消息
                            if ((DateTime.Now.Ticks - _lastProgrammaticScrollTicks) / TimeSpan.TicksPerMillisecond < 1200)
                                return;
                            if (_suppressScrollRef > 0) return;
                            _suppressScrollRef++;
                            try
                            {
                                _ = PreviewWebView.CoreWebView2?.ExecuteScriptAsync(
                                    $"window.__progScroll=true;window.scrollTo(0,(document.documentElement.scrollHeight-document.documentElement.clientHeight)*{pct.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)});setTimeout(function(){{window.__progScroll=false;}},150);");
                                // 延迟清除 suppress，确保对方的防抖 setTimeout 执行时仍被抑制
                                await Task.Delay(200);
                            }
                            finally { _suppressScrollRef--; }
                        });
                    return;
                }
                else if (msg.StartsWith("sh:")) { return; } // legacy

                if (msg.StartsWith("dbg:"))
                {
                    // 临时诊断信息：直接显示到状态栏
                    var info = msg[4..];
                    Dispatcher.Invoke(() => { if (StatusText != null) StatusText.Text = "DBG " + info; });
                    return;
                }

                if (msg.StartsWith("u:"))
                {
                    // Undo/redo state
                    var parts = msg[2..].Split(':');
                    if (parts.Length == 2)
                    {
                        _canUndo = parts[0] == "1";
                        _canRedo = parts[1] == "1";
                        Dispatcher.Invoke(() => CommandManager.InvalidateRequerySuggested());
                    }
                    return;
                }

                // Content changed (format: "c:text" or plain text for backward compat)
                _editorText = msg.StartsWith("c:") ? msg[2..] : msg;
                if (_sidebarOpen && _sidebarTab == "outline") Dispatcher.BeginInvoke(UpdateOutline);
                Dispatcher.Invoke(() =>
                {
                    _isDirty = true;
                    UpdateTitle();
                    UpdateStatusBar();
                    _debounceTimer.Stop();
                    _debounceTimer.Start();
                    CommandManager.InvalidateRequerySuggested();
                });
                // Query undo/redo state after content change
                _ = EditorWebView.CoreWebView2?.ExecuteScriptAsync(
                    "window.chrome.webview.postMessage('u:'+(document.queryCommandEnabled('undo')?1:0)+':'+(document.queryCommandEnabled('redo')?1:0))");
            };

            // Inject initial content and editor event listeners (runs before page scripts)
            await EditorWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.__ic={System.Text.Json.JsonSerializer.Serialize(_editorText)};");
            await EditorWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(@"
                document.addEventListener('DOMContentLoaded', function() {
                    var ed = document.getElementById('editor');
                    if (!ed) return;
                    ed.addEventListener('scroll', function() {
                        if (window.__progScroll) return;
                        clearTimeout(window._est);
                        window._est = setTimeout(function() {
                            if (window.__progScroll) return;
                            var p = ed.scrollHeight > ed.clientHeight ? ed.scrollTop / (ed.scrollHeight - ed.clientHeight) : 0;
                            try { window.chrome.webview.postMessage('s:' + p); } catch(e) {}
                        }, 50);
                    });
                });
            ");
            EditorWebView.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                // Ensure content is set after full page load using current _editorText
                var text = System.Text.Json.JsonSerializer.Serialize(_editorText);
                _ = EditorWebView.CoreWebView2.ExecuteScriptAsync(
                    $"document.getElementById('editor').value={text};window.__ic={text};");
            };
            // Load editor HTML
            var editorHtml = GetEditorHtml(_currentThemeId);
            EditorWebView.NavigateToString(editorHtml);

            UpdatePreview();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"WebView2 初始化失败: {ex.Message}";
        }
    }

    #endregion

    #region Markdown Rendering

    private void UpdatePreview()
    {
        var markdown = _editorText;
        if (string.IsNullOrEmpty(markdown))
        {
            RenderHtml("");
            return;
        }

        try
        {
            var html = Markdown.ToHtml(markdown, _pipeline);
            RenderHtml(html);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"渲染错误: {ex.Message}";
        }
    }

    private async void RenderHtml(string bodyHtml)
    {
        var theme = AllThemes.Find(t => t.Id == _currentThemeId) ?? AllThemes[0];
        var css = BuildCss(theme.Colors);

        // Save preview scroll position before reload
        var scrollPct = 0.0;
        try
        {
            var result = await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                "var m=document.documentElement.scrollHeight-document.documentElement.clientHeight;m>0?document.documentElement.scrollTop/m:0");
            double.TryParse(result, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out scrollPct);
        }
        catch { }

        // Inject heading markers for scroll sync
        var hCount = 0;
        var marked = System.Text.RegularExpressions.Regex.Replace(bodyHtml,
            @"<(h[1-6])\b", m => $"<span id=\"h{hCount++}\"></span><{m.Groups[1].Value}");
        var btnHtml = """
            <button id="qmark-back-to-top" aria-label="Back to top">&#8593;</button>
            <script>
            (function(){
                var btn = document.getElementById('qmark-back-to-top');
                if (!btn) return;
                var showAt = 300;
                function update() {
                    var st = document.documentElement.scrollTop || document.body.scrollTop;
                    if (st > showAt) btn.classList.add('visible');
                    else btn.classList.remove('visible');
                }
                window.addEventListener('scroll', update, { passive: true });
                update();
                btn.addEventListener('click', function(e) {
                    e.stopPropagation();
                    e.preventDefault();
                    try { window.chrome.webview.postMessage('back-to-top'); } catch(e) {}
                });
            })();
            </script>
            """;
        var fullHtml = $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><style>{css}</style></head>
            <body>{marked}{btnHtml}</body>
            </html>
            """;
        if (PreviewWebView.CoreWebView2 != null)
        {
            PreviewWebView.CoreWebView2.NavigateToString(fullHtml);

            // Restore scroll position after navigation completes
            if (scrollPct > 0 && PreviewWebView.CoreWebView2 != null)
            {
                var pct = scrollPct;
                _ = Dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(80);
                    await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.scrollTo(0,(document.documentElement.scrollHeight-document.documentElement.clientHeight)*{pct.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)})");
                });
            }
        }
    }

    #endregion

    #region View Mode

    private void SwitchViewMode(object sender, RoutedEventArgs e)
    {
        if (ModeSource.IsChecked == true)
            _currentViewMode = ViewMode.Source;
        else if (ModeSplit.IsChecked == true)
            _currentViewMode = ViewMode.Split;
        else
            _currentViewMode = ViewMode.Preview;

        UpdateViewMode();
    }

    private void UpdateViewMode()
    {
        if (ColEditor == null || ColSplitter == null || ColPreview == null)
            return;

        switch (_currentViewMode)
        {
            case ViewMode.Source:
                ColEditor.Width = new GridLength(1, GridUnitType.Star);
                ColSplitter.Width = new GridLength(0);
                ColPreview.Width = new GridLength(0);
                StatusText.Text = "源码模式";
                break;
            case ViewMode.Split:
                ColEditor.Width = new GridLength(1, GridUnitType.Star);
                ColSplitter.Width = new GridLength(4);
                ColPreview.Width = new GridLength(1, GridUnitType.Star);
                StatusText.Text = "分栏模式";
                break;
            case ViewMode.Preview:
                ColEditor.Width = new GridLength(0);
                ColSplitter.Width = new GridLength(0);
                ColPreview.Width = new GridLength(1, GridUnitType.Star);
                StatusText.Text = "预览模式";
                break;
        }

        RefreshSidebarSurface();
    }

    private void InitFloatOverlay()
    {
        SidebarFloatIcon = new TextBlock
        {
            Text = "☰",
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        SidebarFloatBtn = new Border
        {
            Width = 34, Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
            Cursor = Cursors.Hand,
            Child = SidebarFloatIcon,
            Visibility = Visibility.Collapsed,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                ShadowDepth = 0, BlurRadius = 10, Opacity = 0.16
            }
        };
        Canvas.SetLeft(SidebarFloatBtn, 0);
        Canvas.SetTop(SidebarFloatBtn, 0);
        SidebarFloatBtn.MouseDown += SidebarFloatBtn_MouseDown;
        SidebarFloatBtn.MouseUp += SidebarFloatBtn_MouseUp;
        SidebarFloatBtn.MouseMove += SidebarFloatBtn_MouseMove;
        SidebarFloatBtn.MouseEnter += SidebarFloatBtn_MouseEnter;
        SidebarFloatBtn.MouseLeave += SidebarFloatBtn_MouseLeave;

        SidebarFloatLayer = new Canvas { Background = Brushes.Transparent };
        SidebarFloatLayer.Children.Add(SidebarFloatBtn);
        // When overlay window resizes (e.g. after maximize), reposition the button
        SidebarFloatLayer.SizeChanged += (_, _) => RepositionSidebarBtn();

        _floatOverlayWin = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Owner = this,
            Content = SidebarFloatLayer,
            Left = Left,
            Top = Top + 88,
            Width = Math.Max(1, ActualWidth),
            Height = Math.Max(1, ActualHeight - 88 - 26),
        };

        // Prevent the overlay from stealing keyboard focus.
        // Mouse clipping is handled by SetWindowRgn in UpdateOverlayRegion().
        _floatOverlayWin.SourceInitialized += (_, _) =>
        {
            var helper = new WindowInteropHelper(_floatOverlayWin);
            var ex = GetWindowLong(helper.Handle, GWL_EXSTYLE);
            SetWindowLong(helper.Handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(helper.Handle)?.AddHook(ResizeOverlayWindowProc);
        };

        _floatOverlayWin.Show();
        UpdateOverlayRegion();

        // Apply current theme color to button and sidebar
        var theme = AllThemes.Find(t => t.Id == _currentThemeId) ?? AllThemes[0];
        _currentColors = theme.Colors;
        UpdateSidebarBtnColor(theme.Colors);
        UpdateSidebarColors(theme.Colors);
    }

    /// <summary>Sync overlay window to cover only the content area (Row 3), not the full window.
    /// Uses calculated offsets so it works immediately without waiting for layout.</summary>
    private void SyncOverlayBounds()
    {
        if (_floatOverlayWin == null || ContentAreaGrid == null) return;
        if (ContentAreaGrid.ActualWidth <= 0 || ContentAreaGrid.ActualHeight <= 0) return;

        try
        {
            var screenPt = ContentAreaGrid.PointToScreen(new Point(0, 0));

            // PointToScreen returns device pixels; Window.Left/Top use logical (DIP) units.
            // Convert device pixels back to DIP so overlay aligns with the real content area.
            var src = PresentationSource.FromVisual(this);
            var transform = src?.CompositionTarget?.TransformFromDevice;
            var dipPt = transform.HasValue ? transform.Value.Transform(screenPt) : screenPt;

            _floatOverlayWin.Left = dipPt.X;
            _floatOverlayWin.Top = dipPt.Y;
            _floatOverlayWin.Width = Math.Max(1, ContentAreaGrid.ActualWidth);
            _floatOverlayWin.Height = Math.Max(1, ContentAreaGrid.ActualHeight);
        }
        catch
        {
            const double topOffset = 88;
            const double bottomOffset = 26;
            _floatOverlayWin.Left = Left;
            _floatOverlayWin.Top = Top + topOffset;
            _floatOverlayWin.Width = Math.Max(1, ActualWidth);
            _floatOverlayWin.Height = Math.Max(1, ActualHeight - topOffset - bottomOffset);
        }
    }

    private void RefreshSidebarSurface()
    {
        SyncOverlayBounds();
        RepositionSidebarBtn();
        if (_sidebarOpen)
        {
            if (_sidebarTab == "files")
                PopulateFileTree();
            else
                UpdateOutline();
        }
    }

    private void RepositionSidebarBtn()
    {
        if (SidebarFloatLayer == null || SidebarFloatBtn == null)
            return;

        if (SidebarFloatLayer.ActualWidth <= 0 || SidebarFloatLayer.ActualHeight <= 0)
            return;

        const double buttonSize = 34;
        const double hiddenPeek = 10;
        var maxX = Math.Max(0, SidebarFloatLayer.ActualWidth - buttonSize);
        var maxY = Math.Max(0, SidebarFloatLayer.ActualHeight - buttonSize);
        var centerY = (SidebarFloatLayer.ActualHeight - buttonSize) / 2;

        if (!_sidebarBtnInitialized)
        {
            _btnOffX = 0;
            _btnOffY = 0;
            _sidebarBtnPinnedLeft = true;
            _sidebarBtnHoverExpanded = false;
            _sidebarBtnInitialized = true;
        }

        var baseX = _btnOffX;
        var baseY = centerY + _btnOffY;

        var clampedX = Math.Max(0, Math.Min(maxX, baseX));
        var clampedY = Math.Max(0, Math.Min(maxY, baseY));

        _btnOffX = clampedX;
        _btnOffY = clampedY - centerY;

        if (_sidebarOpen)
        {
            _sidebarBtnPinnedLeft = true;
            _sidebarBtnAutoHideEnabled = false;
            Canvas.SetLeft(SidebarFloatBtn, 0);
            Canvas.SetTop(SidebarFloatBtn, clampedY);
        }
        else
        {
            _sidebarBtnAutoHideEnabled = true;
            _btnOffX = _sidebarBtnPinnedLeft ? 0 : maxX;
            ApplySidebarBtnPosition(hiddenPeek);
        }

        SidebarFloatBtn.Visibility = Visibility.Visible;
        UpdateOverlayRegion();
    }

    private void ApplySidebarBtnPosition(double hiddenPeek = 10)
    {
        if (SidebarFloatLayer == null || SidebarFloatBtn == null || SidebarFloatIcon == null) return;
        const double buttonSize = 34;
        var maxX = Math.Max(0, SidebarFloatLayer.ActualWidth - buttonSize);
        var maxY = Math.Max(0, SidebarFloatLayer.ActualHeight - buttonSize);
        var centerY = (SidebarFloatLayer.ActualHeight - buttonSize) / 2;

        var visibleX = Math.Max(0, Math.Min(maxX, _btnOffX));
        var visibleY = Math.Max(0, Math.Min(maxY, centerY + _btnOffY));

        bool isPeeking = _sidebarBtnAutoHideEnabled && !_sidebarBtnHoverExpanded;
        if (isPeeking)
        {
            const double tabWidth = 14;
            SidebarFloatBtn.Width = tabWidth;
            SidebarFloatBtn.Height = buttonSize;
            if (_sidebarBtnPinnedLeft)
            {
                SidebarFloatBtn.CornerRadius = new CornerRadius(0, 10, 10, 0);
                SidebarFloatIcon.Text = "›";
                Canvas.SetLeft(SidebarFloatBtn, 0);
            }
            else
            {
                SidebarFloatBtn.CornerRadius = new CornerRadius(10, 0, 0, 10);
                SidebarFloatIcon.Text = "‹";
                Canvas.SetLeft(SidebarFloatBtn, SidebarFloatLayer.ActualWidth - tabWidth);
            }
            Canvas.SetTop(SidebarFloatBtn, visibleY);
        }
        else
        {
            SidebarFloatBtn.Width = buttonSize;
            SidebarFloatBtn.Height = buttonSize;
            SidebarFloatBtn.CornerRadius = new CornerRadius(17);
            if (_sidebarBtnAutoHideEnabled)
                SidebarFloatIcon.Text = "☰";

            Canvas.SetLeft(SidebarFloatBtn, visibleX);
            Canvas.SetTop(SidebarFloatBtn, visibleY);
        }
        UpdateOverlayRegion();
    }

    private void SidebarFloatBtn_MouseDown(object? sender, System.Windows.Input.MouseButtonEventArgs? e)
    {
        if (e == null) return;
        _btnDragging = false;
        _sidebarBtnHoverExpanded = true;
        ApplySidebarBtnPosition();
        var pos = e.GetPosition(SidebarFloatLayer);
        _dragMouseOffX = pos.X - _btnOffX;
        _dragMouseOffY = pos.Y - ((SidebarFloatLayer.ActualHeight - SidebarFloatBtn.Height) / 2 + _btnOffY);
        _dragCheckX = pos.X;
        _dragCheckY = pos.Y;
        SidebarFloatBtn.CaptureMouse();
    }

    private void SidebarFloatBtn_MouseMove(object? sender, System.Windows.Input.MouseEventArgs? e)
    {
        if (e == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(SidebarFloatLayer);
        var dx = pos.X - _dragCheckX;
        var dy = pos.Y - _dragCheckY;

        if (Math.Abs(dx) > 6 || Math.Abs(dy) > 6)
            _btnDragging = true;
        if (!_btnDragging) return;

        var maxX = Math.Max(0, SidebarFloatLayer.ActualWidth - SidebarFloatBtn.Width);
        var maxY = Math.Max(0, SidebarFloatLayer.ActualHeight - SidebarFloatBtn.Height);
        var newX = Math.Max(0, Math.Min(maxX, pos.X - _dragMouseOffX));
        var newY = Math.Max(0, Math.Min(maxY, pos.Y - _dragMouseOffY));

        _sidebarBtnPinnedLeft = newX <= maxX / 2;
        _sidebarBtnAutoHideEnabled = false;
        _btnOffX = newX;
        _btnOffY = newY - (SidebarFloatLayer.ActualHeight - SidebarFloatBtn.Height) / 2;
        ApplySidebarBtnPosition();
    }

    private void SidebarFloatBtn_MouseUp(object? sender, System.Windows.Input.MouseButtonEventArgs? e)
    {
        SidebarFloatBtn.ReleaseMouseCapture();
        if (!_btnDragging)
        {
            ToggleSidebar();
        }
        else
        {
            SnapSidebarBtnToEdge();
        }
        _btnDragging = false;
    }

    private void SidebarFloatBtn_MouseEnter(object? sender, MouseEventArgs e)
    {
        _sidebarBtnHoverExpanded = true;
        ApplySidebarBtnPosition();
    }

    private void SidebarFloatBtn_MouseLeave(object? sender, MouseEventArgs e)
    {
        if (_btnDragging) return;
        _sidebarBtnHoverExpanded = false;
        ApplySidebarBtnPosition();
    }

    private void SnapSidebarBtnToEdge()
    {
        var maxX = Math.Max(0, SidebarFloatLayer.ActualWidth - SidebarFloatBtn.Width);
        if (_sidebarOpen)
        {
            _btnOffX = 0;
            _sidebarBtnPinnedLeft = true;
            _sidebarBtnAutoHideEnabled = false;
        }
        else if (maxX > 0)
        {
            _sidebarBtnPinnedLeft = _btnOffX <= maxX / 2;
            _btnOffX = _sidebarBtnPinnedLeft ? 0 : maxX;
            _sidebarBtnAutoHideEnabled = true;
        }

        _sidebarBtnHoverExpanded = false;
        ApplySidebarBtnPosition();
    }

    private void ToggleSidebar()
    {
        _sidebarOpen = !_sidebarOpen;
        if (_sidebarOpen)
        {
            ColSidebarPanel.Width = new GridLength(220);
            ColSidebarSplitter.Width = new GridLength(4);
            SidebarSplitter.IsEnabled = true;
            SidebarFloatIcon.Text = "✕";
            _sidebarBtnPinnedLeft = true;
            _sidebarBtnHoverExpanded = true;
            _sidebarBtnAutoHideEnabled = false;
            _btnOffX = 0;
            RepositionSidebarBtn();
            if (_sidebarTab == "files")
                ShowFilesTab(null, null);
            else
                ShowOutlineTab(null, null);
        }
        else
        {
            ColSidebarPanel.Width = new GridLength(0);
            ColSidebarSplitter.Width = new GridLength(0);
            SidebarSplitter.IsEnabled = false;
            SidebarFloatIcon.Text = "☰";
            _sidebarBtnPinnedLeft = true;
            _sidebarBtnHoverExpanded = false;
            _sidebarBtnAutoHideEnabled = true;
            _btnOffX = 0;
            RepositionSidebarBtn();
        }
    }

    private void ShowOutlineTab(object? sender, System.Windows.Input.MouseButtonEventArgs? e)
    {
        _sidebarTab = "outline";
        var linkColor = ParseColor(_currentColors?.Link, Color.FromRgb(0x25, 0x63, 0xeb));
        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x6b, 0x72, 0x80));
        TabOutline.BorderBrush = new SolidColorBrush(linkColor);
        ((TextBlock)TabOutline.Child).Foreground = new SolidColorBrush(linkColor);
        ((TextBlock)TabOutline.Child).FontWeight = FontWeights.SemiBold;
        TabFiles.BorderBrush = Brushes.Transparent;
        ((TextBlock)TabFiles.Child).Foreground = new SolidColorBrush(textColor);
        ((TextBlock)TabFiles.Child).FontWeight = FontWeights.Normal;
        OutlineContent.Visibility = Visibility.Visible;
        FilesContent.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(new Action(UpdateOutline), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ShowFilesTab(object? sender, System.Windows.Input.MouseButtonEventArgs? e)
    {
        _sidebarTab = "files";
        var linkColor = ParseColor(_currentColors?.Link, Color.FromRgb(0x25, 0x63, 0xeb));
        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x6b, 0x72, 0x80));
        TabFiles.BorderBrush = new SolidColorBrush(linkColor);
        ((TextBlock)TabFiles.Child).Foreground = new SolidColorBrush(linkColor);
        ((TextBlock)TabFiles.Child).FontWeight = FontWeights.SemiBold;
        TabOutline.BorderBrush = Brushes.Transparent;
        ((TextBlock)TabOutline.Child).Foreground = new SolidColorBrush(textColor);
        ((TextBlock)TabOutline.Child).FontWeight = FontWeights.Normal;
        OutlineContent.Visibility = Visibility.Collapsed;
        FilesContent.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(PopulateFileTree), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateOutline()
    {
        OutlinePanel.Children.Clear();
        _outlineHeadings.Clear();
        if (string.IsNullOrEmpty(_editorText)) return;

        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x33, 0x33, 0x33));
        var hoverBg = ParseColor(_currentColors?.AltRowBg, Color.FromRgb(0xf3, 0xf4, 0xf6));
        var hoverBrush = new SolidColorBrush(hoverBg);

        var lines = _editorText.Split('\n');
        var headingIndex = 0;
        bool inFence = false;
        char fenceChar = '`';
        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimEnd('\r');

            // 跟踪 fenced code block：``` 或 ~~~（最多 3 空格缩进），围栏内不识别标题
            var fenceMatch = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^\s{0,3}(`{3,}|~{3,})");
            if (fenceMatch.Success)
            {
                var marker = fenceMatch.Groups[1].Value[0];
                if (!inFence) { inFence = true; fenceChar = marker; }
                else if (marker == fenceChar) { inFence = false; }
                continue;
            }
            if (inFence) continue;

            // ATX 标题允许最多 3 个前导空格；4+ 空格/tab 是缩进代码块，不是标题
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
            if (!match.Success) continue;

            var heading = new OutlineHeading(
                headingIndex++,
                match.Groups[1].Value.Length,
                match.Groups[2].Value.Trim(),
                i);
            _outlineHeadings.Add(heading);

            var item = new Border
            {
                Padding = new Thickness(4 + heading.Level * 12, 3, 4, 3),
                CornerRadius = new CornerRadius(4),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = heading.Text,
                    FontSize = 12 - heading.Level * 0.5,
                    FontWeight = heading.Level <= 2 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(textColor),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };

            item.MouseEnter += (_, _) => item.Background = hoverBrush;
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseDown += (_, _) => ScrollToHeading(heading);

            OutlinePanel.Children.Add(item);
        }
    }

    private static Color ParseColor(string? hex, Color fallback)
    {
        if (string.IsNullOrEmpty(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return fallback; }
    }

    private async void ScrollToHeading(OutlineHeading heading)
    {
        if (heading.LineIndex < 0) return;

                // 记录程序化滚动时间戳，用于过滤后续的滚动同步消息
        _lastProgrammaticScrollTicks = DateTime.Now.Ticks;

        // 双层守卫：JS 端 window.__progScroll 直接屏蔽 scroll 事件（不再 postMessage），
        // C# 端 _suppressScrollRef 作为兜底，避免任何延迟到达的旧消息触发回写
        _suppressScrollRef++;
        try
        {
            // 编辑器：行号比例 × scrollHeight 直接计算 scrollTop（textarea 自动 wrap 时也成立）
            // 多帧锁定方案：先设置 scrollTop，再用 requestAnimationFrame 连续多帧重置
            // 目的是覆盖：
            //   - focus()/setSelectionRange 引起的 caret-into-view 异步副作用
            //   - 视图模式切换后的布局抖动
            // 这样无论 textarea 是否有焦点，scrollTop 都会被强制锁定到 target
            if (EditorWebView?.CoreWebView2 != null)
            {
                await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                    $"(function(){{"
                    + $"window.__progScroll=true;"
                    + $"var e=document.getElementById('editor');"
                    + $"if(!e){{setTimeout(function(){{window.__progScroll=false;}},800);return;}}"
                    + $"var li={heading.LineIndex};"
                    + $"var text=e.value;"
                    + $"var lineStart=0;"
                    + $"for(var i=0;i<li;i++){{"
                    + $"var nl=text.indexOf('\\n',lineStart);"
                    + $"if(nl<0){{lineStart=text.length;break;}}"
                    + $"lineStart=nl+1;"
                    + $"}}"
                    + $"var totalLines=text.split('\\n').length||1;"
                    + $"var maxScroll=Math.max(0,e.scrollHeight-e.clientHeight);"
                    + $"var target=Math.max(0,(li/totalLines)*maxScroll-e.clientHeight*0.05);"
                    + $"e.scrollTop=target;"
                    + $"try{{e.setSelectionRange(lineStart,lineStart);}}catch(_){{}}"
                    + $"e.scrollTop=target;" // 立即再锁一次，覆盖 setSelectionRange 可能引起的 caret 滚动
                    + $"var n=0;"
                    + $"function lock(){{e.scrollTop=target;n++;if(n<10)requestAnimationFrame(lock);}}"
                    + $"requestAnimationFrame(lock);"
                    + $"setTimeout(function(){{e.scrollTop=target;}},80);"
                    + $"setTimeout(function(){{e.scrollTop=target;}},200);"
                    + $"setTimeout(function(){{e.scrollTop=target;window.__progScroll=false;}},800);"
                    + $"var beforeSt=e.scrollTop;var canScroll=e.scrollHeight>e.clientHeight;"
                    + $"try{{window.chrome.webview.postMessage('dbg:before='+beforeSt+',can='+canScroll+',sh='+e.scrollHeight+',ch='+e.clientHeight+',li='+li+',tl='+totalLines+',tgt='+target+',st='+e.scrollTop);}}catch(_){{}}"
                    + $"}})()");
            }

            // 预览：直接定位到注入的 heading 锚点 #h{Index}，比按百分比同步精确得多
            if (PreviewWebView?.CoreWebView2 != null)
            {
                await PreviewWebView.CoreWebView2.ExecuteScriptAsync(
                    $"(function(){{"
                    + $"window.__progScroll=true;"
                    + $"var t=document.getElementById('h{heading.Index}');"
                    + $"if(t){{var r=t.getBoundingClientRect();"
                    + $"window.scrollTo(0,Math.max(0,window.scrollY+r.top-20));}}"
                    + $"setTimeout(function(){{window.__progScroll=false;}},500);"
                    + $"}})()");
            }
        }
        finally
        {
            await Task.Delay(600);
            _suppressScrollRef--;
        }
    }

    private void PopulateFileTree()
    {
        FileTreeView.Items.Clear();
        FilesDirText.Text = "";
        var dir = !string.IsNullOrEmpty(_currentFilePath)
            ? System.IO.Path.GetDirectoryName(_currentFilePath)
            : null;

        if (dir == null || !Directory.Exists(dir))
        {
            FileTreeView.Items.Add(new TreeViewItem { Header = "请先打开一个文件", IsEnabled = false });
            return;
        }

        FilesDirText.Text = dir;
        var root = CreateDirNode(new DirectoryInfo(dir));
        FileTreeView.Items.Add(root);
        root.IsExpanded = true;
    }

    private void RefreshSidebarContentForCurrentFile()
    {
        if (_sidebarTab == "outline")
            UpdateOutline();
        else if (_sidebarTab == "files")
            PopulateFileTree();
    }

    private TreeViewItem CreateDirNode(DirectoryInfo dirInfo)
    {
        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x33, 0x33, 0x33));
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = "📁 " + dirInfo.Name, FontSize = 12,
                Foreground = new SolidColorBrush(textColor) },
            Tag = dirInfo.FullName
        };
        item.Items.Add("__loading__");
        item.Expanded += DirNode_Expanded;

        var ctx = new System.Windows.Controls.ContextMenu();
        var nf = new System.Windows.Controls.MenuItem { Header = "📄 新建文件" };
        var nd = new System.Windows.Controls.MenuItem { Header = "📁 新建目录" };
        var dp = dirInfo.FullName;
        nf.Click += (_, _) => {
            // Use Dispatcher to ensure ContextMenu has closed before expanding
            Dispatcher.BeginInvoke(() => {
                item.IsExpanded = true;
                InlineCreateItem(dp, item, false);
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
        nd.Click += (_, _) => {
            Dispatcher.BeginInvoke(() => {
                item.IsExpanded = true;
                InlineCreateDir(dp, item);
            }, System.Windows.Threading.DispatcherPriority.Input);
        };
        ctx.Items.Add(nf); ctx.Items.Add(nd);
        item.ContextMenu = ctx;
        return item;
    }

    private void DirNode_Expanded(object? sender, System.Windows.RoutedEventArgs? e)
    {
        if (sender is not TreeViewItem item || item.Tag is not string dirPath) return;
        // 防止重复加载：只有占位符时才真正加载（避免每次展开都重复添加节点）
        if (item.Items.Count == 1 && item.Items[0] is string)
        {
            item.Items.Clear();
            try
            {
                foreach (var sd in new DirectoryInfo(dirPath).GetDirectories())
                    item.Items.Add(CreateDirNode(sd));
                foreach (var f in new DirectoryInfo(dirPath).GetFiles("*.md"))
                    item.Items.Add(CreateFileNode(f));
            }
            catch { }
        }
    }

    private TreeViewItem CreateFileNode(FileInfo fileInfo)
    {
        var fn = fileInfo.Name;
        var ic = fileInfo.FullName == _currentFilePath;
        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x33, 0x33, 0x33));
        var linkColor = ParseColor(_currentColors?.Link, Color.FromRgb(0x25, 0x63, 0xeb));
        var item = new TreeViewItem
        {
            Header = new TextBlock { Text = "📄 " + fn, FontSize = 12,
                FontWeight = ic ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ic ? new SolidColorBrush(linkColor) : new SolidColorBrush(textColor) },
            Tag = fileInfo.FullName
        };
        item.Selected += (_, _) => {
            if (fileInfo.FullName != _currentFilePath) {
                OpenRecentFile(fileInfo.FullName);
                PopulateFileTree();
            }
        };
        var ctx = new System.Windows.Controls.ContextMenu();
        var fp = fileInfo.FullName;
        var rn = new System.Windows.Controls.MenuItem { Header = "✏ 重命名" };
        var cp = new System.Windows.Controls.MenuItem { Header = "📋 复制路径" };
        var dl = new System.Windows.Controls.MenuItem { Header = "🗑 删除" };
        rn.Click += (_, _) => InlineRename(fp, item);
        cp.Click += (_, _) => { try { System.Windows.Clipboard.SetText(fp); } catch { } };
        dl.Click += (_, _) => DeleteFile(fp);
        ctx.Items.Add(rn); ctx.Items.Add(cp);
        ctx.Items.Add(new System.Windows.Controls.Separator());
        ctx.Items.Add(dl);
        item.ContextMenu = ctx;
        return item;
    }

    private void InlineCreateItem(string dirPath, TreeViewItem parent, bool expand)
    {
        var bgColor = ParseColor(_currentColors?.AltRowBg, Color.FromRgb(0xf8, 0xf9, 0xfa));
        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x33, 0x33, 0x33));
        var borderColor = ParseColor(_currentColors?.Border, Color.FromRgb(0xe5, 0xe7, 0xeb));

        var tb = new System.Windows.Controls.TextBox {
            Style = (Style)FindResource("InlineEditBox"),
            Background = new SolidColorBrush(bgColor),
            Foreground = new SolidColorBrush(textColor),
            BorderBrush = new SolidColorBrush(borderColor)
        };
        var ti = new TreeViewItem { Header = tb, IsSelected = true };
        parent.Items.Insert(0, ti);
        if (expand) parent.IsExpanded = true;
        Dispatcher.BeginInvoke(() => tb.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        tb.KeyDown += (_, ke) => {
            if (ke.Key == Key.Enter) {
                var n = tb.Text.Trim();
                if (string.IsNullOrEmpty(n)) { parent.Items.Remove(ti); return; }
                var p = Path.Combine(dirPath, n.EndsWith(".md") ? n : n + ".md");
                try {
                    var d = Path.GetDirectoryName(p);
                    if (d != null) Directory.CreateDirectory(d);
                    File.WriteAllText(p, $"# {Path.GetFileNameWithoutExtension(p)}\n\n");
                    OpenRecentFile(p); PopulateFileTree();
                } catch (Exception ex) { ShowModernDialog("创建失败: " + ex.Message, "错误", isError: true); parent.Items.Remove(ti); }
            } else if (ke.Key == Key.Escape) parent.Items.Remove(ti);
        };
        tb.LostFocus += (_, _) => {
            try {
                if (string.IsNullOrEmpty(tb.Text.Trim()))
                    parent.Items.Remove(ti);
            } catch { }
        };
    }

    private void InlineCreateDir(string parentPath, TreeViewItem parent)
    {
        var bgColor = ParseColor(_currentColors?.AltRowBg, Color.FromRgb(0xf8, 0xf9, 0xfa));
        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x33, 0x33, 0x33));
        var borderColor = ParseColor(_currentColors?.Border, Color.FromRgb(0xe5, 0xe7, 0xeb));

        var tb = new System.Windows.Controls.TextBox {
            Style = (Style)FindResource("InlineEditBox"),
            Background = new SolidColorBrush(bgColor),
            Foreground = new SolidColorBrush(textColor),
            BorderBrush = new SolidColorBrush(borderColor)
        };
        var ti = new TreeViewItem { Header = tb, IsSelected = true };
        parent.Items.Insert(0, ti); parent.IsExpanded = true;
        Dispatcher.BeginInvoke(() => tb.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
        tb.KeyDown += (_, ke) => {
            if (ke.Key == Key.Enter) {
                var n = tb.Text.Trim();
                if (string.IsNullOrEmpty(n)) { parent.Items.Remove(ti); return; }
                try { Directory.CreateDirectory(Path.Combine(parentPath, n)); PopulateFileTree(); }
                catch (Exception ex) { ShowModernDialog("创建失败: " + ex.Message, "错误", isError: true); parent.Items.Remove(ti); }
            } else if (ke.Key == Key.Escape) parent.Items.Remove(ti);
        };
        tb.LostFocus += (_, _) => {
            try {
                if (string.IsNullOrEmpty(tb.Text.Trim()))
                    parent.Items.Remove(ti);
            } catch { }
        };
    }

    private void InlineRename(string filePath, TreeViewItem item)
    {
        var oldName = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        var dir = Path.GetDirectoryName(filePath);
        if (dir == null) return;

        var bgColor = ParseColor(_currentColors?.AltRowBg, Color.FromRgb(0xf8, 0xf9, 0xfa));
        var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x33, 0x33, 0x33));
        var borderColor = ParseColor(_currentColors?.Border, Color.FromRgb(0xe5, 0xe7, 0xeb));

        var tb = new System.Windows.Controls.TextBox {
            Style = (Style)FindResource("InlineEditBox"),
            Text = oldName,
            Background = new SolidColorBrush(bgColor),
            Foreground = new SolidColorBrush(textColor),
            BorderBrush = new SolidColorBrush(borderColor)
        };
        var originalHeader = item.Header;
        item.Header = tb;
        Dispatcher.BeginInvoke(() => {
            tb.Focus();
            tb.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        tb.KeyDown += (_, ke) => {
            if (ke.Key == Key.Enter) {
                var n = tb.Text.Trim();
                if (string.IsNullOrEmpty(n) || n + ext == Path.GetFileName(filePath)) {
                    item.Header = originalHeader; return;
                }
                var newPath = Path.Combine(dir, n + ext);
                try {
                    if (File.Exists(newPath)) {
                        ShowModernDialog("文件已存在: " + n + ext, "错误", isError: true);
                        item.Header = originalHeader; return;
                    }
                    File.Move(filePath, newPath);
                    if (_currentFilePath == filePath) {
                        _currentFilePath = newPath;
                        UpdateFileNameLabel(); UpdateTitle();
                    }
                    PopulateFileTree();
                } catch (Exception ex) {
                    ShowModernDialog("重命名失败: " + ex.Message, "错误", isError: true);
                    item.Header = originalHeader;
                }
            } else if (ke.Key == Key.Escape) item.Header = originalHeader;
        };
        tb.LostFocus += (_, _) => {
            // If still showing TextBox, revert
            if (item.Header is System.Windows.Controls.TextBox)
                item.Header = originalHeader;
        };
    }

    private void DeleteFile(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var isCurrentFile = _currentFilePath == filePath;
        try {
            File.Delete(filePath);
            if (isCurrentFile) {
                _editorText = ""; _currentFilePath = null; _isDirty = false;
                UpdateTitle(); UpdateFileNameLabel(); UpdateStatusBar();
                _ = EditorWebView?.CoreWebView2?.ExecuteScriptAsync("document.getElementById('editor').value=''");
            }
            // 刷新文件树：仅当在文件标签页时才刷新，否则下次进入文件标签页时自动刷新
            if (_sidebarOpen && _sidebarTab == "files")
            {
                Dispatcher.InvokeAsync(() => {
                    try { PopulateFileTree(); } catch { }
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        } catch (Exception ex) { ShowModernDialog("删除失败: " + ex.Message, "错误", isError: true); }
    }

    #endregion

    #region Click-to-Edit

    private void ClickToEditToggled(object sender, RoutedEventArgs e)
    {
        if (StatusText == null) return;
        _clickToEditEnabled = ClickToEditToggle.IsChecked == true;
        StatusText.Text = _clickToEditEnabled ? "点击编辑: 开启" : "点击编辑: 关闭";
        SaveSettings();
    }

    #endregion

    #region Theme

    private void PopulateThemeSelector()
    {
        ThemeSelector.Items.Clear();
        ThemeSelector.SelectedIndex = -1;
        int selectIdx = 0;
        foreach (var theme in AllThemes)
        {
            if (!_enabledThemeIds.Contains(theme.Id)) continue;
            var item = new ComboBoxItem { Content = theme.Name, Tag = theme.Id };
            ThemeSelector.Items.Add(item);
            if (theme.Id == _currentThemeId)
                selectIdx = ThemeSelector.Items.Count - 1;
        }
        if (ThemeSelector.Items.Count > 0)
            ThemeSelector.SelectedIndex = selectIdx;
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeSelector.SelectedItem is not ComboBoxItem item) return;

        var themeId = item.Tag as string;
        ApplyTheme(themeId);
    }

    private void ApplyTheme(string themeId)
    {
        var theme = AllThemes.Find(t => t.Id == themeId);
        if (theme == null) return;

        _currentThemeId = theme.Id;
        _currentColors = theme.Colors;

        // Update editor WebView2 styling via JS
        _ = EditorWebView?.CoreWebView2?.ExecuteScriptAsync(
            $"document.getElementById('editor').style.background='{theme.EditorBg}';" +
            $"document.getElementById('editor').style.color='{theme.EditorFg}';" +
            $"document.body.style.background='{theme.EditorBg}';" +
            $"var s=document.getElementById('sb-style')||document.head.appendChild(document.createElement('style'));" +
            $"s.id='sb-style';s.textContent='::-webkit-scrollbar{{width:8px}}::-webkit-scrollbar-track{{background:transparent}}::-webkit-scrollbar-thumb{{background:{theme.Colors.ScrollThumb};border-radius:4px}}::-webkit-scrollbar-thumb:hover{{background:{theme.Colors.ScrollHover}}}';");

        // Update floating button color to match theme
        UpdateSidebarBtnColor(theme.Colors);
        // Update sidebar panel and tab colors
        UpdateSidebarColors(theme.Colors);
        // Update context menu brushes (defined in App.xaml, used by ContextMenu Popup visual tree)
        UpdateContextMenuBrushes(theme.Colors);

        UpdatePreview();
        StatusText.Text = $"主题已切换: {theme.Name}";
        SaveSettings();
    }

    private void UpdateSidebarBtnColor(ThemeColors colors)
    {
        if (SidebarFloatBtn == null || SidebarFloatIcon == null) return;
        try
        {
            var bgColor = (System.Windows.Media.Color)ColorConverter.ConvertFromString(colors.Border);
            var fgColor = (System.Windows.Media.Color)ColorConverter.ConvertFromString(colors.Link);
            SidebarFloatBtn.Background = new SolidColorBrush(bgColor);
            SidebarFloatIcon.Foreground = new SolidColorBrush(fgColor);
        }
        catch { }
    }

    private void UpdateSidebarColors(ThemeColors colors)
    {
        try
        {
            var bgColor = (System.Windows.Media.Color)ColorConverter.ConvertFromString(colors.Bg);
            var borderColor = (System.Windows.Media.Color)ColorConverter.ConvertFromString(colors.Border);
            var linkColor = (System.Windows.Media.Color)ColorConverter.ConvertFromString(colors.Link);
            var textColor = (System.Windows.Media.Color)ColorConverter.ConvertFromString(colors.Text);

            // Sidebar panel background and border
            SidebarPanel.Background = new SolidColorBrush(bgColor);
            SidebarPanel.BorderBrush = new SolidColorBrush(borderColor);

            // Refresh active tab indicator with new theme color
            var activeTab = _sidebarTab == "files" ? TabFiles : TabOutline;
            var inactiveTab = _sidebarTab == "files" ? TabOutline : TabFiles;
            activeTab.BorderBrush = new SolidColorBrush(linkColor);
            ((TextBlock)activeTab.Child).Foreground = new SolidColorBrush(linkColor);
            inactiveTab.BorderBrush = Brushes.Transparent;
            ((TextBlock)inactiveTab.Child).Foreground = new SolidColorBrush(textColor);

            // Refresh outline items with new text/hover colors
            if (_sidebarOpen && _sidebarTab == "outline") UpdateOutline();

            // Update file tree context menu colors
            if (_sidebarOpen && _sidebarTab == "files") RefreshFileTreeMenuColors();
        }
        catch { }
    }

    private void UpdateContextMenuBrushes(ThemeColors colors)
    {
        try
        {
            var bgColor = ParseColor(colors?.Bg, Colors.White);
            var borderColor = ParseColor(colors?.Border, Color.FromRgb(0xe5, 0xe7, 0xeb));
            var textColor = ParseColor(colors?.Text, Color.FromRgb(0x1a, 0x1a, 0x2e));
            var hoverBg = ParseColor(colors?.AltRowBg, Color.FromRgb(0xf3, 0xf4, 0xf6));
            var accentColor = ParseColor(colors?.Link, Color.FromRgb(0x25, 0x63, 0xeb));
            var altBg = ParseColor(colors?.AltRowBg, Color.FromRgb(0xf8, 0xf9, 0xfa));

            // Context menu brushes (App.xaml, used by Popup visual tree)
            App.Current.Resources["CtxMenuBgBrush"] = new SolidColorBrush(bgColor);
            App.Current.Resources["CtxMenuBorderBrush"] = new SolidColorBrush(borderColor);
            App.Current.Resources["CtxMenuTextBrush"] = new SolidColorBrush(textColor);
            App.Current.Resources["CtxMenuHoverBgBrush"] = new SolidColorBrush(hoverBg);

            // TreeViewItem selection/hover brushes (Window.Resources, DynamicResource)
            this.Resources["TreeItemSelectedBg"] = new SolidColorBrush(hoverBg);
            this.Resources["TreeItemHoverBg"] = new SolidColorBrush(ParseColor(
                colors?.AltRowBg, Color.FromRgb(0xf3, 0xf4, 0xf6)));
        }
        catch { }
    }

    private void RefreshFileTreeMenuColors()
    {
        if (FileTreeView == null) return;
        try
        {
            var textColor = ParseColor(_currentColors?.Text, Color.FromRgb(0x33, 0x33, 0x33));
            var hoverBg = ParseColor(_currentColors?.AltRowBg, Color.FromRgb(0xf3, 0xf4, 0xf6));
            var borderColor = ParseColor(_currentColors?.Border, Color.FromRgb(0xe5, 0xe7, 0xeb));
            var bgColor = ParseColor(_currentColors?.Bg, Colors.White);

            // Update tree view item text colors
            ApplyTreeViewItemColors(FileTreeView.Items, textColor);

            // Refresh the sidebar background to match new theme
            SidebarPanel.Background = new SolidColorBrush(bgColor);
        }
        catch { }
    }

    private void ApplyTreeViewItemColors(System.Collections.IList items, System.Windows.Media.Color textColor)
    {
        foreach (var item in items)
        {
            if (item is System.Windows.Controls.TreeViewItem tvi)
            {
                if (tvi.Header is System.Windows.Controls.TextBlock tb)
                    tb.Foreground = new SolidColorBrush(textColor);
                if (tvi.Items.Count > 0)
                    ApplyTreeViewItemColors(tvi.Items, textColor);
            }
        }
    }

    private static Color GetContrastColor(Color background)
    {
        var luminance = (0.299 * background.R) + (0.587 * background.G) + (0.114 * background.B);
        return luminance > 160 ? Color.FromRgb(0x1f, 0x29, 0x37) : Colors.White;
    }

    private void OpenThemeManager(object? sender, RoutedEventArgs? e)
    {
        ThemePopup.Placement = PlacementMode.Bottom;
        ThemePopup.PlacementTarget = MenuTheme;
        ThemeSearchBox.Text = "";
        RebuildThemePopup();
        ThemePopup.IsOpen = true;
    }

    private void ThemeSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RebuildThemePopup();
    }

    private void RebuildThemePopup()
    {
        ThemeListPanel.Children.Clear();

        var filter = ThemeSearchBox?.Text?.Trim() ?? "";
        var accentBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563eb")!);
        var activeBgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e8f0fe")!);
        var hoverBgBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f3f4f6")!);
        var textBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333")!);
        var whiteBrush = Brushes.White;

        var filteredThemes = string.IsNullOrEmpty(filter)
            ? AllThemes
            : AllThemes.Where(t => t.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        foreach (var theme in filteredThemes)
        {
            var isEnabled = _enabledThemeIds.Contains(theme.Id);
            var isCurrent = theme.Id == _currentThemeId;
            var rowBg = isCurrent ? activeBgBrush : Brushes.Transparent;
            var rowMouseOverBg = isCurrent ? activeBgBrush : hoverBgBrush;

            // Custom flat checkbox
            var checkBoxOuter = new Border
            {
                Width = 16, Height = 16,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1.5),
                BorderBrush = isEnabled ? accentBrush : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#d1d5db")!),
                Background = isEnabled ? accentBrush : Brushes.Transparent,
                VerticalAlignment = VerticalAlignment.Center
            };

            if (isEnabled)
            {
                checkBoxOuter.Child = new TextBlock
                {
                    Text = "✓", FontSize = 11,
                    Foreground = whiteBrush,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var contentStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            contentStack.Children.Add(checkBoxOuter);

            var nameText = new TextBlock
            {
                Text = theme.Name,
                FontSize = 13,
                Foreground = textBrush,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            contentStack.Children.Add(nameText);

            if (isCurrent)
            {
                nameText.FontWeight = FontWeights.SemiBold;
                var currentLabel = new TextBlock
                {
                    Text = "使用中",
                    FontSize = 11,
                    Foreground = accentBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                contentStack.Children.Add(currentLabel);
            }

            var row = new Border
            {
                Background = rowBg,
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Child = contentStack
            };

            // MouseOver effect
            row.MouseEnter += (_, _) =>
            {
                if (!isCurrent)
                    row.Background = hoverBgBrush;
            };
            row.MouseLeave += (_, _) =>
            {
                if (!isCurrent)
                    row.Background = Brushes.Transparent;
            };

            // Click to toggle
            row.MouseDown += (_, _) =>
            {
                if (isEnabled && isCurrent) return; // Can't disable current
                ToggleTheme(theme.Id, !isEnabled);
                RebuildThemePopup();
            };

            ThemeListPanel.Children.Add(row);
        }
    }

    private void ThemeSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in AllThemes) ToggleTheme(t.Id, true);
        RebuildThemePopup();
    }

    private void ThemeDeselectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var t in AllThemes) ToggleTheme(t.Id, t.Id == _currentThemeId);
        RebuildThemePopup();
    }

    private void ToggleTheme(string themeId, bool enabled)
    {
        // Never disable the current theme
        if (!enabled && themeId == _currentThemeId) return;

        // Ensure at least one theme remains enabled
        if (!enabled && _enabledThemeIds.Count <= 1) return;

        if (enabled && !_enabledThemeIds.Contains(themeId))
            _enabledThemeIds.Add(themeId);
        else if (!enabled)
            _enabledThemeIds.Remove(themeId);

        PopulateThemeSelector();
        SaveSettings();
    }

    #endregion

    #region Menu Bar

    private void ShowMenuPopup(Button target, List<(string text, Action? action, bool isChecked)> items)
    {
        var popup = new Popup
        {
            Placement = PlacementMode.Bottom,
            PlacementTarget = target,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = 2,
            PopupAnimation = PopupAnimation.Fade
        };

        var panel = new StackPanel { MinWidth = 140 };

        foreach (var (text, action, isChecked) in items)
        {
            if (text == "-")
            {
                panel.Children.Add(new System.Windows.Shapes.Rectangle { Height = 1, Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e5e7eb")!), Margin = new Thickness(6, 2, 6, 2) });
                continue;
            }

            // Build content: fixed-width check area + label
            var inner = new StackPanel { Orientation = Orientation.Horizontal };

            var checkBlock = new TextBlock
            {
                Text = isChecked ? "✔" : "",
                Width = 18,
                FontSize = 11,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2563eb")!),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            inner.Children.Add(checkBlock);

            inner.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });

            var item = new Button
            {
                Content = inner,
                FontSize = 12,
                Height = 28,
                Padding = new Thickness(8, 0, 12, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#333")!),
                Cursor = Cursors.Hand,
                HorizontalContentAlignment = HorizontalAlignment.Left
            };

            var hoverBg = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#f3f4f6")!);
            item.MouseEnter += (_, _) => item.Background = hoverBg;
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;

            item.Click += (_, _) => { popup.IsOpen = false; action?.Invoke(); };

            panel.Children.Add(item);
        }

        var border = new Border
        {
            Background = Brushes.White,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e5e7eb")!),
            Padding = new Thickness(4),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, Opacity = 0.15 },
            Child = new ScrollViewer { MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, Content = panel }
        };

        popup.Child = border;
        popup.IsOpen = true;
    }

    private void MenuFile_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPopup((Button)sender, new()
        {
            ("新建 (Ctrl+N)", (Action)(() => ApplicationCommands.New.Execute(null, this)), false),
            ("打开 (Ctrl+O)", (Action)(() => ApplicationCommands.Open.Execute(null, this)), false),
            ("保存 (Ctrl+S)", (Action)(() => ApplicationCommands.Save.Execute(null, this)), false),
            ("另存为 (Ctrl+Shift+S)", (Action)(() => ApplicationCommands.SaveAs.Execute(null, this)), false),
            ("-", null, false),
            ("最近打开的文件", (Action)(() => BtnRecent_Click(null, null!)), false),
        });
    }

    private void MenuEdit_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPopup((Button)sender, new()
        {
            ("撤销 (Ctrl+Z)", (Action)(() => ApplicationCommands.Undo.Execute(null, this)), false),
            ("重做 (Ctrl+Y)", (Action)(() => ApplicationCommands.Redo.Execute(null, this)), false),
        });
    }

    private void MenuInsert_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPopup((Button)sender, new()
        {
            ("链接", (Action)(() => FormatLink(null, null!)), false),
            ("图片", (Action)(() => FormatImage(null, null!)), false),
            ("表格", (Action)(() => FormatTable(null, null!)), false),
            ("-", null, false),
            ("代码块", (Action)(() => FormatCode(null, null!)), false),
            ("引用", (Action)(() => FormatQuote(null, null!)), false),
        });
    }

    private void MenuFormat_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPopup((Button)sender, new()
        {
            ("粗体 (Ctrl+B)", (Action)(() => FormatBold(null, null!)), false),
            ("斜体 (Ctrl+I)", (Action)(() => FormatItalic(null, null!)), false),
            ("删除线", (Action)(() => FormatStrike(null, null!)), false),
            ("-", null, false),
            ("标题 1", (Action)(() => ExecJs("window.insertAtLineStart('# ')")), false),
            ("标题 2", (Action)(() => ExecJs("window.insertAtLineStart('## ')")), false),
            ("标题 3", (Action)(() => ExecJs("window.insertAtLineStart('### ')")), false),
            ("标题 4", (Action)(() => ExecJs("window.insertAtLineStart('#### ')")), false),
            ("标题 5", (Action)(() => ExecJs("window.insertAtLineStart('##### ')")), false),
            ("标题 6", (Action)(() => ExecJs("window.insertAtLineStart('###### ')")), false),
            ("-", null, false),
            ("无序列表", (Action)(() => FormatBulletList(null, null!)), false),
            ("有序列表", (Action)(() => FormatNumberedList(null, null!)), false),
        });
    }

    private void MenuView_Click(object sender, RoutedEventArgs e)
    {
        ShowMenuPopup((Button)sender, new()
        {
            ("源码模式", (Action)(() => ModeSource.IsChecked = true), _currentViewMode == ViewMode.Source),
            ("分栏模式", (Action)(() => ModeSplit.IsChecked = true), _currentViewMode == ViewMode.Split),
            ("预览模式", (Action)(() => ModePreview.IsChecked = true), _currentViewMode == ViewMode.Preview),
            ("-", null, false),
            ("点击编辑", (Action)(() => ClickToEditToggle.IsChecked = !ClickToEditToggle.IsChecked), _clickToEditEnabled),
        });
    }

    private void MenuTheme_Click(object sender, RoutedEventArgs e)
    {
        var items = new List<(string, Action?, bool)>();
        foreach (var theme in AllThemes)
        {
            if (!_enabledThemeIds.Contains(theme.Id)) continue;
            items.Add((theme.Name, (Action)(() => SwitchTheme(theme.Id)), theme.Id == _currentThemeId));
        }
        items.Add(("-", null, false));
        items.Add(("管理主题...", (Action)(() => OpenThemeManager(null, null!)), false));
        ShowMenuPopup((Button)sender, items);
    }

    private void SwitchTheme(string themeId)
    {
        ApplyTheme(themeId);
    }

    #endregion

    #region Title & Status

    private void UpdateTitle()
    {
        var name = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "未命名.md";
        var mark = _isDirty ? "* " : "";
        TitleFileName.Text = mark + name;
        Title = mark + name + " - QMark";
    }

    private void UpdateFileNameLabel()
    {
        UpdateTitle();
    }

    private void UpdateStatusBar()
    {
        var text = _editorText;
        var lineCount = text.Split('\n').Length;
        var charCount = text.Length;
        var wordCount = string.IsNullOrWhiteSpace(text) ? 0
            : text.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries).Length;
        WordCountText.Text = $"行 {lineCount} · 字 {wordCount} · 字符 {charCount}";
    }

    #endregion

    #region File Operations

    private async void NewFile(object sender, ExecutedRoutedEventArgs e)
    {
        if (_isDirty && !ConfirmSave()) return;

        _editorText = "";
        _currentFilePath = null;
        _isDirty = false;
        if (EditorWebView?.CoreWebView2 != null)
            await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                $"document.getElementById('editor').value=''");
        UpdateTitle();
        UpdateFileNameLabel();
        UpdateStatusBar();
        UpdatePreview();
        CommandManager.InvalidateRequerySuggested();
        StatusText.Text = "已创建新文件";
    }

    private async void OpenFile(object sender, ExecutedRoutedEventArgs e)
    {
        if (_isDirty && !ConfirmSave()) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
            DefaultExt = ".md"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var content = File.ReadAllText(dialog.FileName, Encoding.UTF8);
                _editorText = content;
                if (EditorWebView?.CoreWebView2 != null)
                    await EditorWebView.CoreWebView2.ExecuteScriptAsync(
                        $"document.getElementById('editor').value={System.Text.Json.JsonSerializer.Serialize(content)}");

                _currentFilePath = dialog.FileName;

                _currentFilePath = dialog.FileName;
                _isDirty = false;
                UpdateTitle();
                UpdateFileNameLabel();
                StatusText.Text = $"已打开: {dialog.FileName}";
                UpdatePreview();
                UpdateStatusBar();
                CommandManager.InvalidateRequerySuggested();
                TrackFileOpen(dialog.FileName);
                SaveLastFilePath(dialog.FileName);
                if (_sidebarOpen && _sidebarTab == "files") Dispatcher.BeginInvoke(PopulateFileTree);
            }
            catch (Exception ex)
            {
                ShowModernDialog($"打开文件失败: {ex.Message}", "错误", isError: true);
            }
        }
    }

    private void SaveFile(object sender, ExecutedRoutedEventArgs e)
    {
        if (_currentFilePath != null)
            SaveToFile(_currentFilePath);
        else
            SaveFileAs(sender, e);
    }

    private void SaveFileAs(object sender, ExecutedRoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Markdown 文件 (*.md)|*.md|所有文件 (*.*)|*.*",
            DefaultExt = ".md"
        };

        if (dialog.ShowDialog() == true)
        {
            _currentFilePath = dialog.FileName;
            SaveToFile(_currentFilePath);
        }
    }

    private void SaveToFile(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, _editorText, Encoding.UTF8);
            _isDirty = false;
            UpdateTitle();
            UpdateFileNameLabel();
            StatusText.Text = $"已保存: {path}";
        }
        catch (Exception ex)
        {
            ShowModernDialog($"保存文件失败: {ex.Message}", "错误", isError: true);
        }
    }

    private bool ConfirmSave()
    {
        var name = _currentFilePath != null ? Path.GetFileName(_currentFilePath) : "未命名.md";
        var result = ShowModernDialog(
            $"\"{name}\" 尚未保存，是否保存更改？",
            "未保存的更改",
            isError: false);

        switch (result)
        {
            case true: // Save
                SaveFile(null!, null!);
                return !_isDirty;
            case false: // Don't Save
                return true;
            default: // Cancel
                return false;
        }
    }

    #endregion

    #region Editing Operations

    private void ExecJs(string js)
    {
        _ = EditorWebView?.CoreWebView2?.ExecuteScriptAsync(js);
    }

    private void Undo(object sender, ExecutedRoutedEventArgs e) => ExecJs("document.execCommand('undo')");
    private void UndoCanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _canUndo;
    private void Redo(object sender, ExecutedRoutedEventArgs e) => ExecJs("document.execCommand('redo')");
    private void RedoCanExecute(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _canRedo;

    private void FormatBold(object sender, RoutedEventArgs e) => ExecJs("window.wrapSelection('**','**')");
    private void FormatItalic(object sender, RoutedEventArgs e) => ExecJs("window.wrapSelection('*','*')");
    private void FormatStrike(object sender, RoutedEventArgs e) => ExecJs("window.wrapSelection('~~','~~')");
    private void FormatHeading(object sender, RoutedEventArgs e) => ExecJs("window.insertAtLineStart('## ')");

    private void FormatCode(object sender, RoutedEventArgs e)
        => ExecJs("var s=editor.selectionStart,e=editor.selectionEnd;if(s!=e)window.wrapSelection('```\\n','\\n```');else window.wrapSelection('`','`');");

    private void FormatBulletList(object sender, RoutedEventArgs e) => ExecJs("window.insertAtLineStart('- ')");
    private void FormatNumberedList(object sender, RoutedEventArgs e) => ExecJs("window.insertAtLineStart('1. ')");
    private void FormatQuote(object sender, RoutedEventArgs e) => ExecJs("window.insertAtLineStart('> ')");

    private void FormatLink(object sender, RoutedEventArgs e)
        => ExecJs("var s=editor.selectionStart,e=editor.selectionEnd;if(s!=e){var t=editor.value.substring(s,e);window.wrapSelection('[',']('+t+')');}else window.insertAtCursor('[链接文本](https://example.com)');");

    private void FormatImage(object sender, RoutedEventArgs e) => ExecJs("window.insertAtCursor('![图片描述](图片路径)')");

    private void FormatTable(object sender, RoutedEventArgs e)
        => ExecJs("window.insertAtCursor('\\n| 列1 | 列2 | 列3 |\\n| --- | --- | --- |\\n| 内容 | 内容 | 内容 |\\n')");

    #endregion

    #region Scroll Sync

    private int _suppressScrollRef;
    private long _lastProgrammaticScrollTicks;

    #endregion

    #region Drag & Drop

    private void Editor_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files.Length > 0 && IsImageFile(files[0]))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        }
    }

    private void Editor_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var file in files)
            {
                if (IsImageFile(file))
                    InsertImage(file);
            }
            e.Handled = true;
        }
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".svg" or ".webp";
    }

    private void InsertImage(string sourcePath)
    {
        try
        {
            var mdDir = _currentFilePath != null
                ? Path.GetDirectoryName(_currentFilePath)!
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            var fileName = $"{Path.GetFileNameWithoutExtension(Path.GetRandomFileName())}{Path.GetExtension(sourcePath)}";
            string destPath, markdownLink;

            if (_currentFilePath != null)
            {
                var imgDir = Path.Combine(mdDir, "assets");
                Directory.CreateDirectory(imgDir);
                destPath = Path.Combine(imgDir, fileName);
                markdownLink = $"![{fileName}](assets/{fileName})";
            }
            else
            {
                destPath = Path.Combine(mdDir, fileName);
                markdownLink = $"![{fileName}]({destPath})";
            }

            File.Copy(sourcePath, destPath, overwrite: false);
            ExecJs($"window.insertAtCursor({JsonSerializer.Serialize(markdownLink)})");
            StatusText.Text = $"已插入图片: {fileName}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"插入图片失败: {ex.Message}";
        }
    }

    #endregion

    #region Modern Dialog

    /// <summary>
    /// Shows a modern-styled modal dialog. Returns true=OK/Save, false=Don't Save, null=Cancel.
    /// </summary>
    private bool? ShowModernDialog(string message, string title, bool isError)
    {
        var dialog = new Window
        {
            Width = 400,
            Height = isError ? 170 : 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true
        };

        bool? result = isError ? true : null;

        // Build UI
        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xf0, 0xf2, 0xf5)),
            CornerRadius = new CornerRadius(10),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) }); // Title bar
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Content
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) }); // Buttons

        // Title bar
        var titleBar = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            CornerRadius = new CornerRadius(10, 10, 0, 0)
        };
        var titleGrid = new Grid();
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleGrid.Margin = new Thickness(12, 0, 0, 0);

        var titleText = new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(titleText, 0);

        var closeBtn = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 12, Foreground = Brushes.White },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 36,
            Height = 36,
            Cursor = Cursors.Hand
        };
        closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(Color.FromRgb(0xe8, 0x11, 0x23));
        closeBtn.MouseLeave += (_, _) => closeBtn.Background = Brushes.Transparent;
        closeBtn.Click += (_, _) => { result = isError ? true : null; dialog.Close(); };
        Grid.SetColumn(closeBtn, 1);

        titleGrid.Children.Add(titleText);
        titleGrid.Children.Add(closeBtn);
        titleBar.Child = titleGrid;
        Grid.SetRow(titleBar, 0);
        grid.Children.Add(titleBar);

        // Content
        var contentPanel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20, 0, 20, 0)
        };
        var msgText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            LineHeight = 22
        };
        contentPanel.Children.Add(msgText);
        var contentBorder = new Border { Child = contentPanel, Background = Brushes.White };
        Grid.SetRow(contentBorder, 1);
        grid.Children.Add(contentBorder);

        // Button bar
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 12, 0)
        };
        var btnBar = new Border
        {
            Child = btnPanel,
            Background = new SolidColorBrush(Color.FromRgb(0xf8, 0xf9, 0xfa)),
            CornerRadius = new CornerRadius(0, 0, 10, 10),
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xe5, 0xe7, 0xeb))
        };

        // Helper to create flat button
        Button MakeButton(string text, bool isPrimary)
        {
            var accentBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xeb));
            var darkBrush = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e));
            var hoverBg = new SolidColorBrush(Color.FromRgb(0xe8, 0xec, 0xf4));

            var btn = new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    Foreground = isPrimary ? Brushes.White : darkBrush
                },
                Background = isPrimary ? accentBrush : new SolidColorBrush(Color.FromRgb(0xf3, 0xf4, 0xf6)),
                BorderThickness = new Thickness(0),
                Height = 30,
                MinWidth = 72,
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Padding = new Thickness(12, 0, 12, 0)
            };
            if (!isPrimary)
            {
                btn.MouseEnter += (_, _) => btn.Background = hoverBg;
                btn.MouseLeave += (_, _) => btn.Background = new SolidColorBrush(Color.FromRgb(0xf3, 0xf4, 0xf6));
            }
            return btn;
        }

        if (isError)
        {
            var okBtn = MakeButton("确定", true);
            okBtn.Click += (_, _) => { result = true; dialog.Close(); };
            btnPanel.Children.Add(okBtn);
        }
        else
        {
            var saveBtn = MakeButton("保存", true);
            saveBtn.Click += (_, _) => { result = true; dialog.Close(); };
            btnPanel.Children.Add(saveBtn);

            var dontSaveBtn = MakeButton("不保存", false);
            dontSaveBtn.Click += (_, _) => { result = false; dialog.Close(); };
            btnPanel.Children.Add(dontSaveBtn);

            var cancelBtn = MakeButton("取消", false);
            cancelBtn.Click += (_, _) => { result = null; dialog.Close(); };
            btnPanel.Children.Add(cancelBtn);
        }

        Grid.SetRow(btnBar, 2);
        grid.Children.Add(btnBar);
        root.Child = grid;
        dialog.Content = root;

        // Handle dialog close via Esc
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (!isError) result = null;
                dialog.Close();
            }
        };

        dialog.ShowDialog();
        return result;
    }

    #endregion

    #region Recent Files

    private static List<RecentFileEntry> LoadRecentFiles()
    {
        try
        {
            if (File.Exists(RecentFilesPath))
            {
                var json = File.ReadAllText(RecentFilesPath);
                var list = JsonSerializer.Deserialize<List<RecentFileEntry>>(json);
                if (list != null)
                {
                    // Filter to only last week
                    var weekAgo = DateTime.Now.AddDays(-7);
                    return list.Where(r => r.OpenedAt >= weekAgo).ToList();
                }
            }
        }
        catch { }
        return new List<RecentFileEntry>();
    }

    private static void SaveRecentFiles(List<RecentFileEntry> files)
    {
        try
        {
            var dir = Path.GetDirectoryName(RecentFilesPath)!;
            Directory.CreateDirectory(dir);
            // Keep only last week entries, max 20 items
            var weekAgo = DateTime.Now.AddDays(-7);
            var filtered = files.Where(r => r.OpenedAt >= weekAgo)
                                .OrderByDescending(r => r.OpenedAt)
                                .Take(20)
                                .ToList();
            File.WriteAllText(RecentFilesPath, JsonSerializer.Serialize(filtered));
        }
        catch { }
    }

    private void TrackFileOpen(string filePath)
    {
        var files = LoadRecentFiles();
        // Remove duplicate if exists
        files.RemoveAll(r => r.Path == filePath);
        // Add to top
        files.Insert(0, new RecentFileEntry(filePath, DateTime.Now));
        SaveRecentFiles(files);
    }

    private void BtnRecent_Click(object sender, RoutedEventArgs e)
    {
        if (RecentPopup.IsOpen)
        {
            RecentPopup.IsOpen = false;
            return;
        }
        RefreshRecentList();
        RecentPopup.IsOpen = true;
    }

    private void RefreshRecentList()
    {
        RecentListPanel.Children.Clear();
        var files = LoadRecentFiles();

        if (files.Count == 0)
        {
            RecentListPanel.Children.Add(new TextBlock
            {
                Text = "暂无最近打开的文件",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6b, 0x72, 0x80)),
                Margin = new Thickness(10, 12, 10, 12),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return;
        }

        foreach (var file in files)
        {
            var item = CreateRecentFileItem(file);
            RecentListPanel.Children.Add(item);
        }
    }

    private Border CreateRecentFileItem(RecentFileEntry entry)
    {
        var itemBorder = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 6, 4, 6),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // File info stack
        var infoPanel = new StackPanel();
        var fileName = System.IO.Path.GetFileName(entry.Path);
        var fileNameText = new TextBlock
        {
            Text = fileName,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x2e)),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        infoPanel.Children.Add(fileNameText);

        var pathText = new TextBlock
        {
            Text = entry.Path,
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };
        infoPanel.Children.Add(pathText);
        Grid.SetColumn(infoPanel, 0);
        grid.Children.Add(infoPanel);

        // Close button (×), hidden by default, shown on hover
        var closeBtn = new Button
        {
            Content = new TextBlock { Text = "×", FontSize = 14, Foreground = new SolidColorBrush(Color.FromRgb(0x6b, 0x72, 0x80)) },
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Width = 24,
            Height = 24,
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "移出最近文件"
        };
        closeBtn.MouseEnter += (_, _) => closeBtn.Background = new SolidColorBrush(Color.FromRgb(0xf3, 0xf4, 0xf6));
        closeBtn.MouseLeave += (_, _) => closeBtn.Background = Brushes.Transparent;
        var capturedPath = entry.Path;
        closeBtn.Click += (_, _) =>
        {
            var files = LoadRecentFiles();
            files.RemoveAll(r => r.Path == capturedPath);
            SaveRecentFiles(files);
            RefreshRecentList();
            // Update status
            StatusText.Text = $"已移除: {System.IO.Path.GetFileName(capturedPath)}";
        };
        Grid.SetColumn(closeBtn, 1);
        grid.Children.Add(closeBtn);

        itemBorder.Child = grid;

        // Hover effects: show close button, highlight background
        itemBorder.MouseEnter += (_, _) =>
        {
            itemBorder.Background = new SolidColorBrush(Color.FromRgb(0xf3, 0xf4, 0xf6));
            closeBtn.Visibility = Visibility.Visible;
        };
        itemBorder.MouseLeave += (_, _) =>
        {
            itemBorder.Background = Brushes.Transparent;
            closeBtn.Visibility = Visibility.Collapsed;
        };

        // Click to open file
        itemBorder.MouseLeftButtonUp += (_, _) =>
        {
            if (File.Exists(entry.Path))
            {
                OpenRecentFile(entry.Path);
                RecentPopup.IsOpen = false;
            }
            else
            {
                ShowModernDialog($"文件不存在:\n{entry.Path}", "错误", isError: true);
                // Remove from list
                var files = LoadRecentFiles();
                files.RemoveAll(r => r.Path == entry.Path);
                SaveRecentFiles(files);
                RefreshRecentList();
            }
        };

        return itemBorder;
    }

    private void OpenRecentFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath, Encoding.UTF8);
            _editorText = content;
            if (EditorWebView?.CoreWebView2 != null)
                _ = EditorWebView.CoreWebView2.ExecuteScriptAsync(
                    $"document.getElementById('editor').value={System.Text.Json.JsonSerializer.Serialize(content)}");

            _currentFilePath = filePath;
            _isDirty = false;
            UpdateTitle();
            UpdateFileNameLabel();
            StatusText.Text = $"已打开: {filePath}";
            UpdatePreview();
            UpdateStatusBar();
            SaveLastFilePath(filePath);
            TrackFileOpen(filePath);
            RefreshSidebarContentForCurrentFile();
        }
        catch (Exception ex)
        {
            ShowModernDialog($"打开文件失败: {ex.Message}", "错误", isError: true);
        }
    }

    #endregion

    #region Window Events

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isDirty)
        {
            if (!ConfirmSave())
                e.Cancel = true;
        }
    }

    #endregion
}
