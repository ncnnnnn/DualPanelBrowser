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

        // 扫描所有解包好的扩展目录（跳过 .disabled）
        var extDirs = new List<string>();
        foreach (var dir in Directory.GetDirectories(extDir))
        {
            if (dir.EndsWith(".disabled")) continue;
            if (File.Exists(Path.Combine(dir, "manifest.json")))
                extDirs.Add(dir);
        }

        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true
        };
        _env = await CoreWebView2Environment.CreateAsync(null, dataDir, options);
        ExtensionManager.PendingExtDirs = extDirs;
        LeftPane.Attach(_env);
        RightPane.Attach(_env);
        LeftPane.StateChanged += (_, _) => SaveSession();
        RightPane.StateChanged += (_, _) => SaveSession();
        LeftPane.GotFocus += (_, _) => { _activePane = LeftPane; UpdatePaneBorder(); };
        RightPane.GotFocus += (_, _) => { _activePane = RightPane; UpdatePaneBorder(); };
        LeftPane.FocusChanged += (_, _) => { _activePane = LeftPane; UpdatePaneBorder(); };
        RightPane.FocusChanged += (_, _) => { _activePane = RightPane; UpdatePaneBorder(); };
        RestoreSession();
        Closed += (_, _) => SaveSession();
    }

    private BrowserPane? _activePane;

    private void UpdatePaneBorder()
    {
        LeftBorder.BorderBrush = _activePane == LeftPane ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Transparent;
        RightBorder.BorderBrush = _activePane == RightPane ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Transparent;
    }

    private void Pane_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _activePane = sender == LeftBorder ? LeftPane : RightPane;
        UpdatePaneBorder();
    }

    private void RestoreSession()
    {
        try
        {
            if (File.Exists(SessionFile))
            {
                var state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(SessionFile));
                if (state != null)
                {
                    LeftPane.LoadUrls(state.Left ?? new List<string>());
                    RightPane.LoadUrls(state.Right ?? new List<string>());
                    return;
                }
            }
        }
        catch { }
        LeftPane.AddTab("about:blank");
        RightPane.AddTab("about:blank");
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
