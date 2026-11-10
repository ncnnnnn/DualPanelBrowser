using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

namespace DualPanelBrowser;

public partial class ExtPopupWindow : Window
{
    private readonly WebView2 _web;

    public ExtPopupWindow(string url, string title, CoreWebView2Environment env, double width = 380, double height = 500)
    {
        Title = title;
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.Manual;
        _web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
        Content = _web;
        Loaded += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.Navigate(url);
        };
        Deactivated += (_, _) => Close();
        Closed += (_, _) => { _web.Dispose(); };
    }
}
