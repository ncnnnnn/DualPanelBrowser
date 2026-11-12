# DualPanelBrowser

基于 **WPF + WebView2** 的 Windows 双面板浏览器：左右分屏浏览、标签页管理、会话恢复、本地广告拦截扩展加载。

## 技术栈

- .NET 8（`net8.0-windows`），WPF + WinForms 互操作
- `Microsoft.Web.WebView2` 1.0.3065.39
- 仅支持 Windows

## 功能

- **左右双面板**：两个独立浏览器面板，拖动中间分隔条调整宽度
- **标签页管理**：每个面板独立标签栏，支持新建、关闭、双击空白处新建；标签可跨面板拖拽且归属跟随当前面板；未选中标签后台预加载
- **会话恢复**：退出时自动保存标签页到 `session.json`，下次启动还原左右两侧标签
- **地址栏**：
  - 已有协议前缀（`http://`、`https://`、`about:`、`chrome-extension://`、`file://`、`data:`）原样访问
  - 命中域名 / IPv4 / localhost（可带端口与路径）自动补 `https://`
  - 其余输入按关键词走 Bing 搜索（`https://www.bing.com/search?q=`，URL 编码）
  - **Ctrl+Enter**：无点的词补成 `www.<词>.com`，有点的补 `www.`，再补 `https://`
  - 地址栏最右侧显示当前页面缩放百分比（切换标签、导航完成、缩放变化时刷新）
- **扩展管理**：加载本地解包扩展目录 / `.crx`，内置扩展管理页面，底部状态栏显示扩展彩色图标
- **焦点与进度**：点击面板高亮蓝色边框，地址栏下方显示加载进度条
- **便携模式**：用户数据、扩展、会话、日志均存放在 exe 同目录，不写系统配置目录

## 效果图

![双面板浏览器界面](docs/screenshot-main.png)

左右双面板布局：标签栏、地址栏（最右侧为缩放百分比）、底部扩展状态栏；蓝色边框标识当前焦点面板。

## 快捷键

| 快捷键 | 作用 |
|---|---|
| `Ctrl+T` | 在焦点面板新建标签 |
| `Ctrl+W` | 关闭焦点面板当前标签 |
| `Enter`（地址栏） | 导航或发起搜索 |
| `Ctrl+Enter`（地址栏） | 补全 `www.` / `.com` 后访问 |

## 编译环境

本机实测环境（其他 Windows 版本可按需对应调整）：

| 项 | 版本 / 说明 |
|---|---|
| 操作系统 | Windows 11 专业版（10.0.22631） |
| .NET SDK | 10.0.404（目标框架 `net8.0-windows`，可向下构建 .NET 8 目标） |
| .NET Desktop Runtime | 8.0.33（运行所需） |
| WebView2 Runtime | 157.0.4341.x（Edge 内核） |
| WebView2 NuGet 包 | `Microsoft.Web.WebView2` 1.0.3065.39 |
| NuGet 源 | 华为云镜像，见 `nuget.config` |

> 项目目标框架为 `net8.0-windows`，需在 Windows 上编译；WPF / WebView2 均不支持 Linux、macOS。

## 编译命令

```bash
# 还原依赖
dotnet restore

# Release 编译
dotnet build -c Release
```

产物：`bin/Release/net8.0-windows/DualPanelBrowser.exe`。

`extmanager.html` 与 `assets/ext_*.png` 由工程文件在编译时自动复制到输出目录。

## 目录结构

```
DualPanelBrowser-dev/
├── DualPanelBrowser.csproj   # 工程文件
├── nuget.config              # NuGet 镜像配置
├── README.md
├── .gitignore
├── src/                      # 源码
│   ├── App.xaml              # 应用入口
│   ├── MainWindow.xaml(.cs)  # 主窗口（双面板布局、会话保存/还原、全局快捷键）
│   ├── BrowserPane.xaml(.cs) # 单侧面板（工具栏、标签栏、地址栏、缩放显示）
│   ├── ExtensionManager.cs    # 扩展解包与加载
│   ├── ExtPopupWindow.cs      # 扩展 popup 弹窗
│   ├── ExtensionsWindow.xaml(.cs) # 旧版扩展弹窗
│   └── extmanager.html       # 扩展管理页面（内嵌 HTML）
└── assets/                   # 图标资源
    ├── app.ico
    ├── ext_adblockplus.png
    ├── ext_adblockultimate.png
    ├── ext_adguard.png
    ├── ext_ublock.png
    └── icon-source.png
```

## 运行时文件（exe 同目录，不纳入版本库）

| 路径 | 说明 |
|---|---|
| `Data/` | WebView2 用户数据目录 |
| `Extensions/` | 解包后的扩展目录及 `load.log` 加载日志 |
| `session.json` | 上次会话标签页记录 |
| `extdebug.log` | 诊断日志（地址解析、扩展加载等打点） |

## 已知限制

- 仅 Windows：WPF 与 WebView2 均为 Windows 专用，无 Linux/macOS 嵌入层
- 扩展为本地解包加载，不接入 Chrome 网上应用店；第三方扩展 API 兼容性以实测为准
