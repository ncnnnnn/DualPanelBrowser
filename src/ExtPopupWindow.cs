using System;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DualPanelBrowser;

public partial class ExtPopupWindow : Window
{
    private readonly WebView2 _web;
    private readonly Action<string>? _openTab;
    private readonly string _initialUrl;
    private bool _initialNav;
    private bool _closed;

    public ExtPopupWindow(string url, string title, CoreWebView2Environment env,
        Action<string>? openTab = null, double width = 380, double height = 500)
    {
        Title = title;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual;
        _initialUrl = url;
        _openTab = openTab;
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        Content = _web;
        Loaded += async (_, _) =>
        {
            try
            {
                await _web.EnsureCoreWebView2Async(env);
                if (_closed) return;
                HookEvents();
                _web.CoreWebView2.Navigate(url);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { }
        };
        Closed += (_, _) =>
        {
            _closed = true;
            try { _web.Dispose(); } catch { }
        };
    }

    private void HookEvents()
    {
        var cw = _web.CoreWebView2;
        // window.open / target=_blank / openOptionsPage：拦下来路由到主面板标签
        cw.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _openTab?.Invoke(e.Uri);
        };
        // 同帧导航：首个 popup.html 放行；其后的外链 / 扩展选项页取消并开标签
        cw.NavigationStarting += (_, e) =>
        {
            if (!_initialNav) { _initialNav = true; return; }
            if (_openTab == null) return;
            if (!IsRoutable(e.Uri)) return;
            e.Cancel = true;
            _openTab.Invoke(e.Uri);
        };
    }

    private bool IsRoutable(string u)
    {
        if (string.IsNullOrEmpty(u) || u == _initialUrl) return false;
        if (u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return true;
        // 扩展自身选项页：与初始 popup 路径不同的 chrome-extension 页面
        if (u.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            return !u.StartsWith(_initialUrl, StringComparison.OrdinalIgnoreCase);
        return false;
    }
}
