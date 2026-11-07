using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace DualPanelBrowser;

public partial class MainWindow : Window
{
    private CoreWebView2Environment _env = null!;

    private string AppDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DualPanelBrowser");

    private string SessionFile => Path.Combine(AppDir, "session.json");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var dataDir = Path.Combine(AppDir, "Data");
        Directory.CreateDirectory(dataDir);
        var extDir = Path.Combine(AppDir, "Extensions");
        Directory.CreateDirectory(extDir);

        // 自动解包所有 .crx 文件到同名子目录
        foreach (var crx in Directory.GetFiles(extDir, "*.crx"))
        {
            var name = Path.GetFileNameWithoutExtension(crx);
            var targetDir = Path.Combine(extDir, name);
            if (!Directory.Exists(targetDir) || !File.Exists(Path.Combine(targetDir, "manifest.json")))
            {
                try { await ExtensionManager.ExtractCrxAsync(crx); }
                catch { /* 解包失败跳过 */ }
            }
        }

        // 扫描所有解包好的扩展目录（跳过 .disabled），拼成 --load-extension
        var loadPaths = new List<string>();
        foreach (var dir in Directory.GetDirectories(extDir))
        {
            if (dir.EndsWith(".disabled")) continue;
            if (File.Exists(Path.Combine(dir, "manifest.json")))
                loadPaths.Add(dir);
        }
        var args = "--enable-features=Extensions";
        if (loadPaths.Count > 0)
            args += " --load-extension=" + string.Join(",", loadPaths);

        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = args,
            AreBrowserExtensionsEnabled = true
        };
        _env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
        LeftPane.Attach(_env);
        RightPane.Attach(_env);
        LeftPane.StateChanged += (_, _) => SaveSession();
        RightPane.StateChanged += (_, _) => SaveSession();
        LeftPane.GotFocus += (_, _) => UpdatePaneBorder();
        RightPane.GotFocus += (_, _) => UpdatePaneBorder();
        RestoreSession();
    }

    private void UpdatePaneBorder()
    {
        var focused = FocusManager.GetFocusedElement(this) as DependencyObject;
        var pane = PaneUnderFocus(focused);
        LeftBorder.BorderBrush = pane == LeftPane ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Transparent;
        RightBorder.BorderBrush = pane == RightPane ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Transparent;
    }

    private void RestoreSession()
    {
        try
        {
            if (File.Exists(SessionFile))
            {
                var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionFile));
                if (state != null &&
                    ((state.Left?.Count ?? 0) > 0 || (state.Right?.Count ?? 0) > 0))
                {
                    LeftPane.LoadUrls(state.Left ?? new List<string>());
                    RightPane.LoadUrls(state.Right ?? new List<string>());
                    return;
                }
            }
        }
        catch { }
        LeftPane.AddTab("https://www.bing.com");
        RightPane.AddTab("https://www.bing.com");
    }

    private void SaveSession()
    {
        try
        {
            var state = new SessionState
            {
                Left = LeftPane.GetTabUrls(),
                Right = RightPane.GetTabUrls()
            };
            File.WriteAllText(SessionFile, JsonSerializer.Serialize(state));
        }
        catch { }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        var pane = PaneUnderFocus(FocusManager.GetFocusedElement(this) as DependencyObject)
                   ?? LeftPane;
        if (e.Key == Key.T)
        {
            pane.AddTab("about:blank");
            e.Handled = true;
        }
        else if (e.Key == Key.W)
        {
            pane.CloseCurrent();
            e.Handled = true;
        }
    }

    private static BrowserPane? PaneUnderFocus(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is BrowserPane bp) return bp;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private class SessionState
    {
        public List<string> Left { get; set; } = new();
        public List<string> Right { get; set; } = new();
    }
}
