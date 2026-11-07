using System.Windows;
using WinForms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;

namespace DualPanelBrowser;

public partial class ExtensionsWindow : Window
{
    public event Action<string>? OpenUrlRequested;

    public ExtensionsWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await RefreshListAsync();
    }

    private async Task RefreshListAsync()
    {
        await Task.Run(() =>
        {
            Dispatcher.Invoke(() =>
            {
                ExtList.ItemsSource = ExtensionManager.ListExtensions();
            });
        });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshListAsync();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WinForms.FolderBrowserDialog
        {
            Description = "选择包含 .crx 扩展包的目录"
        };
        if (dlg.ShowDialog() != WinForms.DialogResult.OK) return;
        StatusText.Text = "正在安装...";
        var (installed, errors) = await ExtensionManager.InstallFromFolderAsync(dlg.SelectedPath);
        var parts = new List<string>();
        if (installed.Count > 0) parts.Add(string.Join(", ", installed));
        if (errors.Count > 0) parts.Add("失败: " + string.Join(" | ", errors));
        StatusText.Text = parts.Count > 0 ? string.Join("  ", parts) : "没有找到 .crx";
        await RefreshListAsync();
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ExtensionInfo info) return;
        ExtensionManager.SetEnabled(info, !info.IsEnabled);
        await RefreshListAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ExtensionInfo info) return;
        var r = MessageBox.Show($"确定删除扩展 \"{info.Name}\"？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        ExtensionManager.Remove(info);
        await RefreshListAsync();
    }

    private void Options_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn) return;
        if (btn.Tag is not ExtensionInfo info) return;
        if (string.IsNullOrEmpty(info.OptionsPage)) return;
        var url = $"chrome-extension://{info.Id}/{info.OptionsPage}";
        OpenUrlRequested?.Invoke(url);
    }
}
