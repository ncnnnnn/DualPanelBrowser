# DualPanelBrowser

基于 WebView2 的 WPF 双面板浏览器，支持左右分屏浏览、标签页管理、会话恢复和扩展管理。

## 功能

- **左右双面板**：两个独立浏览器面板，可拖动分隔条调整宽度
- **标签页管理**：每个面板独立标签栏，支持新建、关闭、双击空白处新建
- **会话恢复**：关闭时自动保存标签页，下次启动恢复
- **扩展管理**：支持加载本地 .crx 扩展包（广告拦截），内置扩展管理标签页
- **焦点高亮**：点击任一面板自动高亮蓝色边框
- **加载进度条**：地址栏下方蓝色进度条
- **快捷键**：Ctrl+T 新建标签，Ctrl+W 关闭标签

## 编译

```bash
dotnet build -c Release
```

产物：`bin/Release/net8.0-windows/DualPanelBrowser.exe`

## 要求

- .NET 8 SDK
- WebView2 Runtime（Edge 内核）

## 扩展

将 `.crx` 文件放入 `%LOCALAPPDATA%\DualPanelBrowser\Extensions\` 目录，启动时自动解包加载。

## 项目结构

```
WebView2DualPanel/
├── DualPanelBrowser.csproj   # 工程文件
├── nuget.config              # NuGet 镜像配置
├── README.md
├── .gitignore
├── src/                      # 源码
│   ├── App.xaml              # 应用入口
│   ├── MainWindow.xaml(.cs)  # 主窗口（双面板布局）
│   ├── BrowserPane.xaml(.cs) # 单侧面板（工具栏+标签栏+地址栏）
│   ├── ExtensionManager.cs    # 扩展管理（CRX解包+加载）
│   ├── ExtensionsWindow.xaml  # 旧版扩展弹窗（已弃用）
│   └── extmanager.html       # 扩展管理页面（内嵌HTML）
└── assets/                   # 资源文件
    └── app.ico               # 应用图标
```
