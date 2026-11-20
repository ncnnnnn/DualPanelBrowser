using System.IO;
using System.Runtime.InteropServices;
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
    private System.Windows.Threading.DispatcherTimer? _hoverTimer;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private string AppDir => AppDomain.CurrentDomain.BaseDirectory;

    private string SessionFile => Path.Combine(AppDir, "session.json");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Dispatcher.UnhandledException += (_, ev) =>
        {
            try
            {
                var ex = ev.Exception;
                File.AppendAllText(Path.Combine(AppDir, "extdebug.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [DISP] {ex}\n{ex.StackTrace}\n");
                System.Windows.MessageBox.Show($"{ex.GetType().Name}: {ex.Message}\n\n{ex.StackTrace}",
                    "未处理异常", MessageBoxButton.OK, MessageBoxImage.Error);
                ev.Handled = true;
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            try
            {
                var ex = ev.ExceptionObject as Exception;
                File.AppendAllText(Path.Combine(AppDir, "extdebug.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [UNHANDLED] {ex}\n{ex?.StackTrace}\n");
                System.Windows.MessageBox.Show($"{ex?.GetType().Name}: {ex?.Message}\n\n{ex?.StackTrace}",
                    "致命异常", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        };
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
            AreBrowserExtensionsEnabled = true,
            AdditionalBrowserArguments = "--enable-gpu-rasterization --disable-features=msSmartScreenProtection --disable-background-networking --disable-component-update"
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

        // 轮询光标位置激活面板（覆盖 WebView2 子 HWND 吞掉 WPF MouseEnter 的情况）
        _hoverTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hoverTimer.Tick += HoverTick;
        _hoverTimer.Start();
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

    private void Pane_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var pane = sender == LeftBorder ? LeftPane : RightPane;
        if (_activePane != pane)
        {
            _activePane = pane;
            UpdatePaneBorder();
        }
        pane.FocusWebView();
    }

    private void HoverTick(object? sender, EventArgs e)
    {
        if (!IsLoaded) return;
        if (!GetCursorPos(out var pt)) return;
        BrowserPane? hover = null;
        if (PointInPanel(LeftBorder, pt.X, pt.Y)) hover = LeftPane;
        else if (PointInPanel(RightBorder, pt.X, pt.Y)) hover = RightPane;
        if (hover != null && hover != _activePane)
        {
            _activePane = hover;
            UpdatePaneBorder();
            hover.FocusWebView();
        }
    }

    private bool PointInPanel(System.Windows.Controls.Border b, int px, int py)
    {
        if (b.ActualWidth <= 0 || b.ActualHeight <= 0) return false;
        var dpi = VisualTreeHelper.GetDpi(this);
        var top = b.PointToScreen(new System.Windows.Point(0, 0));
        var rect = new Rect(top.X, top.Y, b.ActualWidth * dpi.DpiScaleX, b.ActualHeight * dpi.DpiScaleY);
        return rect.Contains(px, py);
    }

    private void Pane_DragEnter(object sender, System.Windows.DragEventArgs e) => Pane_DragOver(sender, e);

    private void Pane_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        var payload = e.Data.GetData(typeof(TabDragPayload)) as TabDragPayload;
        var target = sender == LeftBorder ? LeftPane : RightPane;
        if (payload == null || payload.Source == target)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            HighlightDrop(sender as System.Windows.Controls.Border, false);
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.Move;
            HighlightDrop(sender as System.Windows.Controls.Border, true);
        }
        e.Handled = true;
    }

    private void Pane_DragLeave(object sender, System.Windows.DragEventArgs e) => HighlightDrop(sender as System.Windows.Controls.Border, false);

    private void Pane_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var payload = e.Data.GetData(typeof(TabDragPayload)) as TabDragPayload;
        var targetBorder = sender as System.Windows.Controls.Border;
        HighlightDrop(targetBorder, false);
        if (payload == null) return;
        var target = targetBorder == LeftBorder ? LeftPane : RightPane;
        if (payload.Source == target) return;
        payload.Source.DetachTab(payload.Tab);
        target.AdoptTab(payload.Tab, payload.Source);
        e.Handled = true;
    }

    private void HighlightDrop(System.Windows.Controls.Border? border, bool on)
    {
        if (border == null) return;
        border.BorderBrush = on ? System.Windows.Media.Brushes.DodgerBlue : System.Windows.Media.Brushes.Transparent;
        if (!on) UpdatePaneBorder();
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
                    if (state.ActivateNewTab is bool an)
                    {
                        LeftPane.ActivateNewTab = an;
                        RightPane.ActivateNewTab = an;
                    }
                    if (state.DedupPopupTabs is bool dd)
                    {
                        LeftPane.DedupPopupTabs = dd;
                        RightPane.DedupPopupTabs = dd;
                    }
                    if (state.PasswordAutofill is bool pw)
                    {
                        LeftPane.PasswordAutofill = pw;
                        RightPane.PasswordAutofill = pw;
                    }
                    if (state.CloseSequence is int cs && cs >= 0 && cs <= 2)
                    {
                        var cb = (BrowserPane.CloseBehavior)cs;
                        LeftPane.CloseSequence = cb;
                        RightPane.CloseSequence = cb;
                    }
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
                Right = RightPane.GetTabUrls(),
                ActivateNewTab = LeftPane.ActivateNewTab,
                DedupPopupTabs = LeftPane.DedupPopupTabs,
                PasswordAutofill = LeftPane.PasswordAutofill,
                CloseSequence = (int)LeftPane.CloseSequence
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
        public bool? ActivateNewTab { get; set; }
        public bool? DedupPopupTabs { get; set; }
        public bool? PasswordAutofill { get; set; }
        public int? CloseSequence { get; set; }
    }
}
