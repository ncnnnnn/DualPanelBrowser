using System.IO;
using System.Text.Json;
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

    public event EventHandler? StateChanged;
    public event EventHandler? FocusChanged;

    public BrowserPane()
    {
        InitializeComponent();
        Tabs.SelectionChanged += (_, _) =>
        {
            var wv = Current;
            string u = "";
            if (wv?.CoreWebView2 != null)
                u = wv.CoreWebView2.Source ?? "";
            else if (wv?.Source != null)
                u = wv.Source.ToString();
            AddressBox.Text = (u == "about:blank" || u == "about:config" || u.StartsWith("data:text/html") || string.IsNullOrEmpty(u)) ? "" : u;
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
            if (Tabs.SelectedItem is TabItem ti && ti.Content is WebView2 wv)
                return wv;
            return null;
        }
    }

    public void AddTab(string url)
    {
        var wv = new WebView2();
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var title = new TextBlock { Text = "New tab", Margin = new Thickness(2, 0, 6, 0) };
        var close = new Button
        {
            Content = "x",
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            FontSize = 10,
            ToolTip = "Close tab"
        };
        var tab = new TabItem { Header = header };
        header.Children.Add(title);
        header.Children.Add(close);
        tab.Content = wv;
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        AddressBox.Text = url == "about:blank" ? "" : url;
        AddressBox.Focus();

        _ = InitAsync(wv, tab, title, url);
        close.Click += (_, _) => CloseTab(tab);
        NotifyStateChanged();
    }

    private async Task InitAsync(WebView2 wv, TabItem tab, TextBlock title, string url)
    {
        await wv.EnsureCoreWebView2Async(_env);
        await ExtensionManager.LoadExtensionsAsync(wv.CoreWebView2);
        wv.GotFocus += (_, _) => FocusChanged?.Invoke(this, EventArgs.Empty);
        wv.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            if (e.TryGetWebMessageAsString() == "__focus__")
                FocusChanged?.Invoke(this, EventArgs.Empty);
        };
        wv.CoreWebView2.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            // 拦截扩展自动弹出的欢迎页/安装页
            if (!e.Uri.StartsWith("chrome-extension://"))
                AddTab(e.Uri);
        };
        wv.CoreWebView2.DocumentTitleChanged += (_, _) =>
            title.Text = wv.CoreWebView2.DocumentTitle;
        wv.CoreWebView2.NavigationStarting += (_, _) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                ProgressBar.BeginAnimation(WidthProperty, null);
                ProgressBar.Width = 0;
                var anim = new System.Windows.Media.Animation.DoubleAnimation(0, ActualWidth,
                    TimeSpan.FromSeconds(3)) { EasingFunction = new System.Windows.Media.Animation.QuadraticEase() };
                ProgressBar.BeginAnimation(WidthProperty, anim);
            });
        };
        wv.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            Dispatcher.InvokeAsync(() =>
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
            if (!AddressBox.IsFocused)
            {
                var u = wv.Source?.ToString() ?? "";
                AddressBox.Text = (u == "about:blank" || u == "about:config") ? "" : u;
            }
            wv.CoreWebView2.ExecuteScriptAsync(
                "document.addEventListener('mousedown',()=>{chrome.webview.postMessage('__focus__')},true);");
            NotifyStateChanged();
        };
        wv.CoreWebView2.Navigate(url);
    }

    private async Task OpenExtManagerAsync()
    {
        // 如果已经打开过，直接切过去
        if (_extMgrWv != null)
        {
            foreach (TabItem ti in Tabs.Items)
            {
                if (ti.Content == _extMgrWv) { Tabs.SelectedItem = ti; return; }
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
        tab.Content = wv;
        Tabs.Items.Add(tab);
        Tabs.SelectedItem = tab;
        close.Click += (_, _) => CloseTab(tab);

        await wv.EnsureCoreWebView2Async(_env);
        _extMgrWv = wv;

        wv.CoreWebView2.WebMessageReceived += OnExtMessage;

        var html = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "extmanager.html"));
        wv.CoreWebView2.NavigateToString(html);
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
        var exts = ExtensionManager.LoadedExts.Select(e => new
        {
            id = e.Id,
            name = e.Name,
            enabled = e.IsEnabled,
            optionsPage = GetOptionsPageForDir(e.Id)
        }).ToList();
        var json = JsonSerializer.Serialize(new { method = "list", extensions = exts });
        _extMgrWv.CoreWebView2.PostWebMessageAsString(json);
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
        var wv = tab.Content as WebView2;
        if (wv == _extMgrWv) _extMgrWv = null;
        if (Tabs.Items.Count <= 1)
        {
            wv?.CoreWebView2?.NavigateToString("<html></html>");
            AddressBox.Text = "";
            NotifyStateChanged();
            return;
        }
        Tabs.Items.Remove(tab);
        wv?.Dispose();
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
            if (ti.Content is WebView2 wv)
            {
                var u = wv.Source?.ToString();
                if (!string.IsNullOrEmpty(u) && u != "about:blank" &&
                    !u!.StartsWith("data:text/html") && !u!.Contains("extmanager"))
                    list.Add(u);
            }
        }
        return list;
    }

    public void LoadUrls(IEnumerable<string> urls)
    {
        foreach (var u in urls)
            AddTab(u);
        if (Tabs.Items.Count == 0)
            AddTab("about:blank");
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Current?.CoreWebView2?.GoBack();
    private void Fwd_Click(object sender, RoutedEventArgs e) => Current?.CoreWebView2?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => Current?.CoreWebView2?.Reload();
    private void ClearAddr_Click(object sender, RoutedEventArgs e) { AddressBox.Clear(); AddressBox.Focus(); }
    private void NewTab_Click(object sender, RoutedEventArgs e) => AddTab("about:blank");
    private void Ext_Click(object sender, RoutedEventArgs e) => _ = OpenExtManagerAsync();

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = AddressBox.Text.Trim();
        if (text.Length == 0) return;
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        Current?.CoreWebView2?.Navigate(text);
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
