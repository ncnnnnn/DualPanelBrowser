using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using WinForms = System.Windows.Forms;

namespace DualPanelBrowser;

public partial class BrowserPane : UserControl
{
    private CoreWebView2Environment _env = null!;
    private WebView2? _extMgrWv;
    private readonly Dictionary<WebView2, string> _tabUrls = new();
    private readonly Dictionary<TabItem, WebView2> _tabWv = new();
    private readonly Dictionary<WebView2, System.Windows.Threading.DispatcherTimer> _tabTimers = new();
    private readonly Dictionary<WebView2, (TabItem tab, TextBlock title, string url)> _pending = new();
    private readonly HashSet<WebView2> _inited = new();
    private bool _batchAdding;
    private bool _preloadStarted;
    private Grid? _viewHost;
    private Grid ViewHost => _viewHost ??= Tabs.Template?.FindName("ViewHost", Tabs) as Grid
        ?? throw new InvalidOperationException("ViewHost 未在 TabControl 模板中找到");

    public event EventHandler? StateChanged;
    public event EventHandler? FocusChanged;

    public BrowserPane()
    {
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) =>
        {
            var wv = Current;
            string u = "";
            try
            {
                if (wv?.CoreWebView2 != null)
                    u = wv.CoreWebView2.Source ?? "";
                else if (wv?.Source != null)
                    u = wv.Source.ToString();
                else if (wv != null && _tabUrls.TryGetValue(wv, out var stored))
                    u = stored;
            }
            catch (ObjectDisposedException) { u = ""; }
            AddressBox.Text = (u == "about:blank" || u == "about:config" || u.StartsWith("data:text/html") || string.IsNullOrEmpty(u)) ? "" : u;
            SyncVisibility();
            EnsureSelectedInit();
            FocusWebView();
            UpdateZoomLabel();
            NotifyStateChanged();
        };
    }

    public void Attach(CoreWebView2Environment env)
    {
        _env = env;
    }

    private void UserControl_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TabBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        FocusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void TabBar_PreviewMouseLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
            AddTab("about:blank");
    }

    public WebView2? Current
    {
        get
        {
            if (Tabs.SelectedItem is TabItem ti && _tabWv.TryGetValue(ti, out var wv))
                return wv;
            return null;
        }
    }

    private void UpdateZoomLabel()
    {
        double zf = 1.0;
        try
        {
            var wv = Current;
            if (wv != null && wv.CoreWebView2 != null) zf = wv.ZoomFactor;
        }
        catch (ObjectDisposedException) { }
        ZoomLabel.Text = $"{(int)Math.Round(zf * 100)}%";
    }

    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetFocus();

    private void EnsureSelectedInit()
    {
        if (_batchAdding) return;
        if (Current is WebView2 wv && _pending.TryGetValue(wv, out var p))
        {
            _pending.Remove(wv);
            Trace($"EnsureSelectedInit url={p.url}");
            _ = InitAsync(wv, p.tab, p.title, p.url);
        }
    }

    private void SyncVisibility()
    {
        var cur = Current;
        foreach (TabItem ti in Tabs.Items)
            if (_tabWv.TryGetValue(ti, out var wv))
                wv.Visibility = wv == cur ? Visibility.Visible : Visibility.Hidden;
    }

    private async void BackgroundPreload()
    {
        if (_preloadStarted) return;
        _preloadStarted = true;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        while (true)
        {
            WebView2? next = null;
            foreach (TabItem ti in Tabs.Items)
                if (_tabWv.TryGetValue(ti, out var wv) && _pending.ContainsKey(wv)) { next = wv; break; }
            if (next == null) break;
            if (_pending.TryGetValue(next, out var p))
            {
                _pending.Remove(next);
                Trace($"BackgroundPreload url={p.url}");
                await InitAsync(next, p.tab, p.title, p.url);
            }
            await Task.Delay(600);
        }
    }

    public void FocusWebView()
    {
        var wv = Current;
        if (wv == null) return;
        try
        {
            wv.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    wv.Focus();
                    if (wv.Handle != IntPtr.Zero) SetFocus(wv.Handle);
                    Trace($"FocusWebView handle=0x{wv.Handle.ToInt64():X} focus=0x{GetFocus().ToInt64():X}");
                }
                catch (ObjectDisposedException) { }
            }), System.Windows.Threading.DispatcherPriority.Input);
        }
        catch (ObjectDisposedException) { }
    }

    private void RunOnCurrent(Action<CoreWebView2> action)
    {
        var wv = Current;
        if (wv == null) return;
        try { var c = wv.CoreWebView2; if (c != null) action(c); }
        catch (ObjectDisposedException) { }
    }

    public void DetachTab(TabItem tab)
    {
        Trace($"DetachTab idx={Tabs.Items.IndexOf(tab)} count={Tabs.Items.Count}");
        if (_tabWv.TryGetValue(tab, out var dwv)) ViewHost.Children.Remove(dwv);
        Tabs.Items.Remove(tab);
        Trace($"DetachTab after count={Tabs.Items.Count} stillContains={Tabs.Items.Contains(tab)}");
    }

    public void AdoptTab(TabItem tab, BrowserPane source)
    {
        Trace($"AdoptTab from pane#{source.GetHashCode()} srcCount={source.Tabs.Items.Count} myCount={Tabs.Items.Count}");
        if (source._tabWv.TryGetValue(tab, out var wv))
        {
            source._tabWv.Remove(tab);
            _tabWv[tab] = wv;
            if (source._tabUrls.TryGetValue(wv, out var u)) { source._tabUrls.Remove(wv); _tabUrls[wv] = u; }
            if (source._tabTimers.TryGetValue(wv, out var t)) { source._tabTimers.Remove(wv); _tabTimers[wv] = t; }
            if (source._pending.TryGetValue(wv, out var p)) { source._pending.Remove(wv); _pending[wv] = p; }
            if (source._inited.Remove(wv)) _inited.Add(wv);
        }
        tab.Tag = this;
        Tabs.Items.Add(tab);
        if (_tabWv.TryGetValue(tab, out var awv)) ViewHost.Children.Add(awv);
        Tabs.SelectedItem = tab;
        SyncVisibility();
        Trace($"AdoptTab done myCount={Tabs.Items.Count} contains={Tabs.Items.Contains(tab)}");
        NotifyStateChanged();
    }

    private void Trace(string msg)
    {
        try { File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extdebug.log"),
            $"[{DateTime.Now:HH:mm:ss.fff}] pane#{GetHashCode()} {msg}\n"); } catch { }
    }

    private TabItem? _dragSource;
    private System.Windows.Point _dragStart;

    private void AttachTabDrag(TabItem tab)
    {
        tab.AllowDrop = true;
        tab.Tag = this;
        tab.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragSource = tab;
            _dragStart = e.GetPosition(Tabs);
        };
        tab.PreviewMouseMove += (_, e) =>
        {
            if (_dragSource != tab || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(Tabs);
            if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
            var payload = new TabDragPayload { Source = (BrowserPane)tab.Tag!, Tab = tab };
            DragDrop.DoDragDrop(Tabs, payload, System.Windows.DragDropEffects.Move);
            _dragSource = null;
        };
        tab.Drop += (_, e) =>
        {
            if (_dragSource == null || _dragSource == tab) return;
            var src = _dragSource;
            int si = Tabs.Items.IndexOf(src);
            int ti = Tabs.Items.IndexOf(tab);
            if (si < 0 || ti < 0) return;
            Tabs.Items.RemoveAt(si);
            if (ti > si) ti--;
            Tabs.Items.Insert(ti, src);
            Tabs.SelectedItem = src;
            e.Handled = true;
        };
    }

    public void AddTab(string url)
    {
        var wv = new WebView2();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = "New tab", Margin = new Thickness(0,0,8,0), FontSize = 12, MaxWidth = 160 };
        var close = new Button
        {
            Content = "✕",
            Width = 16,
            Height = 16,
            Padding = new Thickness(0),
            FontSize = 9,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Close tab"
        };
        var tab = new TabItem { Header = header };
        header.Children.Add(title);
        header.Children.Add(close);
        tab.Content = null;
        _tabWv[tab] = wv;
        ViewHost.Children.Add(wv);
        _tabUrls[wv] = url;
        Tabs.Items.Add(tab);
        Trace($"AddTab url={url} count={Tabs.Items.Count}");
        AttachTabDrag(tab);
        _pending[wv] = (tab, title, url);
        Tabs.SelectedItem = tab;
        AddressBox.Text = url == "about:blank" ? "" : url;
        AddressBox.Focus();

        SyncVisibility();
        EnsureSelectedInit();
        close.Click += (_, _) =>
        {
            var owner = (BrowserPane?)tab.Tag;
            Trace($"close click tagPane={owner?.GetHashCode()} ownerContains={owner != null && owner.Tabs.Items.Contains(tab)} ownerCount={owner?.Tabs.Items.Count}");
            owner?.CloseTab(tab);
        };
        NotifyStateChanged();
    }

    private async Task InitAsync(WebView2 wv, TabItem tab, TextBlock title, string url)
    {
        if (_inited.Contains(wv)) return;
        _inited.Add(wv);
        Trace($"InitAsync start url={url}");
        try { await wv.EnsureCoreWebView2Async(_env); }
        catch (Exception ex) { Trace($"Ensure FAILED url={url} {ex}"); return; }
        Trace($"InitAsync core ready url={url}");
        ExtensionManager.SharedEnv ??= _env;
        // 先注册事件，再加载扩展，才能拦截扩展欢迎页
        wv.GotFocus += (_, _) => ((BrowserPane?)tab.Tag)?.FocusChanged?.Invoke(this, EventArgs.Empty);
        wv.ZoomFactorChanged += (_, _) => Dispatcher.Invoke(UpdateZoomLabel);
        wv.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            if (e.TryGetWebMessageAsString() == "__focus__")
                ((BrowserPane?)tab.Tag)?.FocusChanged?.Invoke(this, EventArgs.Empty);
        };
        wv.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (!e.Uri.StartsWith("chrome-extension://"))
                ((BrowserPane?)tab.Tag)?.AddTab(e.Uri);
        };
        wv.CoreWebView2.NavigationStarting += (s, e) =>
        {
            if (e.Uri.StartsWith("chrome-extension://") &&
                (e.Uri.Contains("welcome") || e.Uri.Contains("installed") ||
                 e.Uri.Contains("success") || e.Uri.Contains("onboard")))
            {
                e.Cancel = true;
            }
        };
        // 扩展后台异步加载，不阻塞页面（必须在UI线程调用AddBrowserExtensionAsync）
        StatusLeft.Text = "扩展加载中... 0s";
        var loadSw = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var n = ExtensionManager.LoadedExts.Count;
            StatusLeft.Text = n == 0
                ? $"扩展加载中... {loadSw.ElapsedMilliseconds / 1000}s"
                : $"扩展加载中 ({n}/4)... {loadSw.ElapsedMilliseconds / 1000}s";
        };
        timer.Start();
        _tabTimers[wv] = timer;
        _ = LoadExtAsync(wv, timer, loadSw);
        wv.CoreWebView2.DocumentTitleChanged += (_, _) =>
            title.Text = wv.CoreWebView2.DocumentTitle;
        wv.CoreWebView2.NavigationStarting += (_, navArgs) =>
        {
            if (navArgs.Uri == "about:blank" || string.IsNullOrEmpty(navArgs.Uri)) return;
            Dispatcher.Invoke(() =>
            {
                ProgressBar.BeginAnimation(WidthProperty, null);
                ProgressBar.Width = 0;
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0, ActualWidth,
                    TimeSpan.FromSeconds(4)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
                ProgressBar.BeginAnimation(WidthProperty, anim);
            });
        };
        wv.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(ProgressBar.ActualWidth, ActualWidth,
                    TimeSpan.FromMilliseconds(300));
                anim.Completed += (_, _) =>
                {
                    var fade = new System.Windows.Media.Animation.DoubleAnimation(ActualWidth, 0,
                        TimeSpan.FromMilliseconds(300));
                    fade.Completed += (_, _) => ProgressBar.BeginAnimation(WidthProperty, null);
                    ProgressBar.BeginAnimation(WidthProperty, fade);
                };
                ProgressBar.BeginAnimation(WidthProperty, anim);
            });
        };
        wv.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            var src = wv.CoreWebView2?.Source ?? wv.Source?.ToString() ?? "";
            Trace($"NavCompleted src={src}");
            _tabUrls[wv] = src;
            if (!AddressBox.IsFocused)
            {
                var u = wv.Source?.ToString() ?? "";
                AddressBox.Text = (u == "about:blank" || u == "about:config") ? "" : u;
            }
            wv.CoreWebView2.ExecuteScriptAsync(
                "document.addEventListener('mousedown',()=>{chrome.webview.postMessage('__focus__')},true);");
            UpdateZoomLabel();
            NotifyStateChanged();
        };
        // 手动触发进度条（首次导航时 ActualWidth 可能还没布局好）；空白页不触发
        if (url != "about:blank")
        {
            Trace($"Navigate url={url}");
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ProgressBar.BeginAnimation(WidthProperty, null);
                ProgressBar.Width = 0;
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0, ActualWidth,
                    TimeSpan.FromSeconds(4)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
                ProgressBar.BeginAnimation(WidthProperty, anim);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        wv.CoreWebView2.Navigate(url);
        BackgroundPreload();
    }

    private async Task OpenExtManagerAsync()
    {
        // 如果已经打开过，直接切过去
        if (_extMgrWv != null)
        {
            foreach (TabItem ti in Tabs.Items)
            {
                if (_tabWv.TryGetValue(ti, out var ewv) && ewv == _extMgrWv) { Tabs.SelectedItem = ti; return; }
            }
            _extMgrWv = null;
        }

        var wv = new WebView2();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = "扩展管理", Margin = new Thickness(2, 0, 6, 0) };
        var close = new Button
        {
            Content = "x",
            Width = 18, Height = 18, Padding = new Thickness(0), FontSize = 10
        };
        var tab = new TabItem { Header = header };
        header.Children.Add(title);
        header.Children.Add(close);
        tab.Content = null;
        _tabWv[tab] = wv;
        ViewHost.Children.Add(wv);
        _tabUrls[wv] = "extmanager";
        Tabs.Items.Add(tab);
        AttachTabDrag(tab);
        Tabs.SelectedItem = tab;
        close.Click += (_, _) =>
        {
            var owner = (BrowserPane?)tab.Tag;
            Trace($"close click tagPane={owner?.GetHashCode()} ownerContains={owner != null && owner.Tabs.Items.Contains(tab)} ownerCount={owner?.Tabs.Items.Count}");
            owner?.CloseTab(tab);
        };

        await wv.EnsureCoreWebView2Async(_env);
        _extMgrWv = wv;

        wv.CoreWebView2.WebMessageReceived += OnExtMessage;

        var html = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extmanager.html"));
        wv.CoreWebView2.NavigateToString(html);
        // 轮询推送扩展列表，直到扩展加载完成
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(2000);
                if (ExtensionManager.LoadedExts.Count > 0)
                {
                    Dispatcher.Invoke(SendExtList);
                    break;
                }
            }
        });
    }

    private async void OnExtMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<JsonElement>(args.WebMessageAsJson);
            var method = msg.GetProperty("method").GetString();

            switch (method)
            {
                case "list":
                    SendExtList();
                    break;
                case "install":
                    await DoInstallAsync();
                    break;
                case "toggle":
                    var tid = msg.GetProperty("id").GetString()!;
                    var tenabled = msg.GetProperty("enabled").GetBoolean();
                    var text = ExtensionManager.LoadedExts.FirstOrDefault(e => e.Id == tid);
                    if (text != null) { await text.EnableAsync(tenabled); }
                    SendExtList();
                    PostExtStatus(tenabled ? "已启用" : "已禁用");
                    break;
                case "remove":
                    var rid = msg.GetProperty("id").GetString()!;
                    var rext = ExtensionManager.LoadedExts.FirstOrDefault(e => e.Id == rid);
                    if (rext != null) { await rext.RemoveAsync(); ExtensionManager.LoadedExts.Remove(rext); }
                    SendExtList();
                    PostExtStatus("已删除");
                    break;
                case "options":
                    var oid = msg.GetProperty("id").GetString()!;
                    var op = GetOptionsPageForDir(oid);
                    if (!string.IsNullOrEmpty(op))
                    {
                        Dispatcher.Invoke(() => AddTab($"chrome-extension://{oid}/{op}"));
                    }
                    break;
            }
        }
        catch { }
    }

    private void SendExtList()
    {
        if (_extMgrWv?.CoreWebView2 == null) return;
        File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extdebug.log"),
            $"SendExtList called, LoadedExts count={ExtensionManager.LoadedExts.Count}\n");
        var exts = ExtensionManager.LoadedExts.GroupBy(e => e.Id).Select(g => g.First()).Select(e => new
        {
            id = e.Id,
            name = e.Name,
            enabled = e.IsEnabled,
            optionsPage = GetOptionsPageForDir(e.Id)
        }).ToList();
        var json = System.Text.Json.JsonSerializer.Serialize(new { method = "list", extensions = exts });
        _extMgrWv.CoreWebView2.PostWebMessageAsString(json);
        // 同时用JS直接注入，确保到达
        _extMgrWv.CoreWebView2.ExecuteScriptAsync($"window.renderList && window.renderList({System.Text.Json.JsonSerializer.Serialize(exts)});");
    }

    private string GetOptionsPageForDir(string extId)
    {
        foreach (var pair in ExtensionManager.ExtDirMapping)
        {
            if (pair.Value == extId)
            {
                var manifestPath = Path.Combine(pair.Key, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                        if (doc.RootElement.TryGetProperty("options_page", out var op))
                            return op.GetString() ?? "";
                    }
                    catch { }
                }
            }
        }
        return "";
    }

    private void PostExtStatus(string text)
    {
        if (_extMgrWv?.CoreWebView2 == null) return;
        var json = JsonSerializer.Serialize(new { method = "status", text = text });
        _extMgrWv.CoreWebView2.PostWebMessageAsString(json);
    }

    private async Task DoInstallAsync()
    {
        await Dispatcher.InvokeAsync(async () =>
        {
            using var dlg = new WinForms.FolderBrowserDialog
            {
                Description = "选择包含 .crx 扩展包的目录"
            };
            if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
            var (installed, errors) = await ExtensionManager.InstallFromFolderAsync(dlg.SelectedPath);
            var parts = new List<string>();
            if (installed.Count > 0) parts.Add(string.Join(", ", installed));
            if (errors.Count > 0) parts.Add("失败: " + string.Join(" | ", errors));
            PostExtStatus(parts.Count > 0 ? string.Join("  ", parts) : "没有找到 .crx");
            SendExtList();
        });
    }

    public void CloseTab(TabItem tab)
    {
        _tabWv.TryGetValue(tab, out var wv);
        Trace($"CloseTab entry idx={Tabs.Items.IndexOf(tab)} count={Tabs.Items.Count} wv={(wv == null ? "null" : "ok")}");
        if (wv == _extMgrWv) _extMgrWv = null;
        if (Tabs.Items.Count <= 1)
        {
            Trace("CloseTab last-tab blanked");
            try { wv?.CoreWebView2?.NavigateToString("<html></html>"); }
            catch (ObjectDisposedException) { }
            AddressBox.Text = "";
            NotifyStateChanged();
            return;
        }
        if (wv != null && _tabTimers.TryGetValue(wv, out var t)) { t.Stop(); _tabTimers.Remove(wv); }
        Tabs.Items.Remove(tab);
        Trace($"CloseTab after remove count={Tabs.Items.Count} stillContains={Tabs.Items.Contains(tab)}");
        _tabUrls.Remove(wv!);
        _tabWv.Remove(tab);
        _pending.Remove(wv!);
        _inited.Remove(wv!);
        ViewHost.Children.Remove(wv!);
        try { wv?.Dispose(); }
        catch (ObjectDisposedException) { }
        NotifyStateChanged();
    }

    public void CloseCurrent()
    {
        if (Tabs.SelectedItem is TabItem ti) CloseTab(ti);
    }

    public List<string> GetTabUrls()
    {
        var list = new List<string>();
        foreach (TabItem ti in Tabs.Items)
        {
            if (_tabWv.TryGetValue(ti, out var wv))
            {
                string? u = null;
                try
                {
                    _tabUrls.TryGetValue(wv, out var stored);
                    u = stored ?? wv.CoreWebView2?.Source ?? wv.Source?.ToString();
                }
                catch (ObjectDisposedException) { }
                if (!string.IsNullOrEmpty(u) && u != "about:blank" &&
                    !u!.StartsWith("data:text/html") && !u!.Contains("extmanager"))
                    list.Add(u);
            }
        }
        return list;
    }

    public void LoadUrls(IEnumerable<string> urls)
    {
        _batchAdding = true;
        foreach (var u in urls)
            AddTab(u);
        _batchAdding = false;
        EnsureSelectedInit();
        if (Tabs.Items.Count == 0)
            AddTab("about:blank");
    }

    private void Back_Click(object sender, RoutedEventArgs e) => RunOnCurrent(c => c.GoBack());
    private void Fwd_Click(object sender, RoutedEventArgs e) => RunOnCurrent(c => c.GoForward());
    private void Reload_Click(object sender, RoutedEventArgs e) => RunOnCurrent(c => c.Reload());
    private void ClearAddr_Click(object sender, RoutedEventArgs e) { AddressBox.Clear(); AddressBox.Focus(); }
    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab("about:blank");
    private async Task LoadExtAsync(WebView2 wv, System.Windows.Threading.DispatcherTimer timer, System.Diagnostics.Stopwatch loadSw)
    {
        CoreWebView2? cw;
        try { cw = wv.CoreWebView2; }
        catch (ObjectDisposedException) { return; }
        if (cw == null) return;
        try
        {
            await ExtensionManager.LoadExtensionsAsync(cw);
            await ExtensionManager.WaitForLoadAsync();
        }
        catch (ObjectDisposedException) { return; }
        timer.Stop();
        StatusLeft.Text = $"扩展加载完成，耗时 {loadSw.ElapsedMilliseconds / 1000}s";
        PopulateExtBar();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private void PopulateExtBar()
    {
        ExtBar.Children.Clear();
        var loadingText = new TextBlock { Text = "扩展加载中...", FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88,0x88,0x88)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4,0,0,0) };
        if (ExtensionManager.LoadedExts.Count == 0)
        {
            ExtBar.Children.Add(new TextBlock { Text = "无扩展", FontSize = 12, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xAA,0xAA,0xAA)), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4,0,0,0) });
            return;
        }
        foreach (var ext in ExtensionManager.LoadedExts)
        {
            var dir = ExtensionManager.ExtDirMapping.FirstOrDefault(x => x.Value == ext.Id).Key;
            System.Windows.Media.ImageSource? icon = null;
            // 优先用 assets 里的彩色图标
            var assetDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets");
            var nameLower = ext.Name.ToLower();
            string? assetFile = null;
            if (nameLower.Contains("adblock plus") || nameLower.Contains("abp")) assetFile = "ext_adblockplus.png";
            else if (nameLower.Contains("ultimate")) assetFile = "ext_adblockultimate.png";
            else if (nameLower.Contains("adguard")) assetFile = "ext_adguard.png";
            else if (nameLower.Contains("ublock")) assetFile = "ext_ublock.png";
            if (assetFile != null)
            {
                var ap = Path.Combine(assetDir, assetFile);
                if (File.Exists(ap))
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(ap, UriKind.Absolute);
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    icon = bmp;
                }
            }
            // fallback: 从扩展 manifest 读
            if (icon == null && !string.IsNullOrEmpty(dir))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                        var root = doc.RootElement;
                        string? iconFile = null;
                        if (root.TryGetProperty("icons", out var icons))
                        {
                            foreach (var size in new[] { "48", "128", "32", "16" })
                            {
                                if (icons.TryGetProperty(size, out var f)) { iconFile = f.GetString(); break; }
                            }
                        }
                        if (iconFile != null)
                        {
                            var iconPath = Path.GetFullPath(Path.Combine(dir, iconFile.Replace('/', Path.DirectorySeparatorChar)));
                            if (File.Exists(iconPath))
                            {
                                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                                bmp.BeginInit();
                                bmp.UriSource = new Uri(iconPath, UriKind.Absolute);
                                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                                bmp.EndInit();
                                bmp.Freeze();
                                icon = bmp;
                            }
                        }
                    }
                    catch { }
                }
            }

            var btn = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(2, 0, 0, 0),
                ToolTip = ext.Name,
                Content = icon != null
                    ? new System.Windows.Controls.Image { Source = icon, Width = 20, Height = 20, Stretch = System.Windows.Media.Stretch.Uniform }
                    : new TextBlock { Text = "🧩", FontSize = 16 },
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            btn.Style = (Style)FindResource("ExtBtn");
            var menu = new ContextMenu();
            var miOptions = new MenuItem { Header = "选项" };
            var info = ExtensionManager.ListExtensions().FirstOrDefault(x => x.Id == ext.Id);
            if (info != null && !string.IsNullOrEmpty(info.OptionsPage))
            {
                miOptions.Click += (_, _) => AddTab($"chrome-extension://{ext.Id}/{info.OptionsPage}");
            }
            else { miOptions.IsEnabled = false; }
            var miPopup = new MenuItem { Header = ext.Name, IsEnabled = false };
            var miManage = new MenuItem { Header = "管理扩展..." };
            miManage.Click += (_, _) => Ext_Click(null!, null!);
            menu.Items.Add(miPopup);
            menu.Items.Add(new Separator());
            menu.Items.Add(miOptions);
            menu.Items.Add(miManage);
            btn.ContextMenu = menu;
            btn.Click += (_, e) =>
            {
                // 读取 popup 路径
                var popupPath = "";
                if (!string.IsNullOrEmpty(dir))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
                        var root = doc.RootElement;
                        if (root.TryGetProperty("action", out var act) && act.TryGetProperty("default_popup", out var pp))
                            popupPath = pp.GetString() ?? "";
                        else if (root.TryGetProperty("browser_action", out var ba) && ba.TryGetProperty("default_popup", out var pp2))
                            popupPath = pp2.GetString() ?? "";
                    }
                    catch { }
                }
                if (!string.IsNullOrEmpty(popupPath))
                {
                    var url = $"chrome-extension://{ext.Id}/{popupPath}";
                    var window = new ExtPopupWindow(url, ext.Name, ExtensionManager.SharedEnv!);
                    var screen = System.Windows.SystemParameters.WorkArea;
                    var btnPos = btn.PointToScreen(new System.Windows.Point(0, 0));
                    window.Left = btnPos.X - window.Width + btn.ActualWidth;
                    window.Top = btnPos.Y - window.Height - 4;
                    if (window.Top < screen.Top) window.Top = btnPos.Y + btn.ActualHeight + 4;
                    window.Show();
                }
                else if (info != null && !string.IsNullOrEmpty(info.OptionsPage))
                {
                    AddTab($"chrome-extension://{ext.Id}/{info.OptionsPage}");
                }
            };
            ExtBar.Children.Add(btn);
        }
    }

    private void ExtMenu_Click(object sender, RoutedEventArgs e)
    {
        if (BtnExt.ContextMenu != null)
        {
            BtnExt.ContextMenu.PlacementTarget = BtnExt;
            BtnExt.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            BtnExt.ContextMenu.IsOpen = true;
        }
    }

    private void Ext_Click(object sender, RoutedEventArgs e) => _ = OpenExtManagerAsync();

    // 域名 / IPv4 / localhost（可带端口与路径），用于区分网址与搜索词
    private static readonly Regex _hostRegex = new Regex(
        @"^(localhost|(\d{1,3}\.){3}\d{1,3}|[a-zA-Z0-9][a-zA-Z0-9-]*(\.[a-zA-Z0-9][a-zA-Z0-9-]*)+)(:\d{1,5})?(/[^\s]*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ResolveAddress(string raw, bool ctrlEnter)
    {
        var t = raw.Trim();
        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            t.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return t;

        if (ctrlEnter)
        {
            // Ctrl+Enter：补 www. 与 .com（已有域名的只补 www.）
            if (!t.Contains('.')) t = "www." + t + ".com";
            else if (!t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "www." + t;
            return "https://" + t;
        }

        if (_hostRegex.IsMatch(t)) return "https://" + t;
        return "https://www.bing.com/search?q=" + Uri.EscapeDataString(t);
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = AddressBox.Text.Trim();
        if (text.Length == 0) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        var url = ResolveAddress(text, ctrl);
        Trace($"Address resolve ctrl={ctrl} text={text} -> {url}");
        RunOnCurrent(c => c.Navigate(url));
        e.Handled = true;
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}

public class TabDragPayload
{
    public BrowserPane Source = null!;
    public TabItem Tab = null!;
}
