# 路线 B：标签页与页面生命周期对照（GreenBrowser → WPF + WebView2）

> 目的：以 GreenBrowser 3.1 源码为"功能规格参考"，逐条对齐标签生命周期行为，
> 作为在 DualPanelBrowser（WPF + WebView2）上重写的需求依据。
> 所有 GreenBrowser 结论均标注源码位置；"待核实"项明确标出，不臆测。
>
> **进度**：第一批三项（后台标签、关闭回退、重复链接去重）已实现并验证，
> 实现位置与验证证据见各节末尾的「已实现」小节；第二批五项尚未开工。

## 0. 结论速览

> 状态标记：✅ 已实现并验证 ／ ⏳ 部分实现 ／ ⬜ 待做

| 行为 | GreenBrowser 默认 | 现有 DualPanelBrowser | 路线 B 状态 |
|---|---|---|---|
| 链接落点 | 当前页（`LinkInNewWindow=0`） | 当前页；`window.open`/`target=_blank` 一律新标签 | ⏳ 落点开关/右键三态未做；重复 URL 去重已做 |
| 后台打开 | 支持，默认后台（`ActiveNew=0`） | 一律前台 | ✅ 已实现（默认仍为前台，可切换） |
| 失败重试 | **无自动重试** | 无 | ⬜ 未做（属新增设计） |
| 关闭回退 | 默认左邻（`CloseSequence=2`），可配右邻/末尾；回源标签；可撤销 | 依赖 WPF `TabControl` 默认（右邻） | ⏳ 左邻/右邻/末尾 + 来源标签已做；撤销栈未做 |
| 空白标签复用 | 有（`FilteBlank`，默认关） | 无 | ⬜ 未做 |
| 弹窗/重复过滤 | 四类过滤器默认全开 | 仅扩展页导航拦截 | ⏳ 链接去重已做；四类过滤器未做 |

---

## 1. 页面新链接：跟随当前页还是开新标签

### GreenBrowser 源码事实

- 开关变量 `g_bLinkInNewWindow`，配置项 `LinkInNewWindow`，**默认 0**（`Src\MainFrm.cpp:11340`，`GetProfileInt("Settings","LinkInNewWindow",0)`）。
- 分流点 `OnBeforeNavigate2`：`Src\GreenBrowserView.cpp:646`
  ```
  if(g_bLinkInNewWindow || m_bLock || m_nInNewWindow)
  ```
  三者全不成立时，不新建标签，直接在当前视图导航 —— 即**默认跟随当前页**。
- 新开的四条排除规则（`:681-716`）：
  1. `m_bFirst` 为真（首屏导航）不开新标签；
  2. 同页锚点 `#anchor`（判断 `strncmp(lpszURL, m_lpszUrl, pdest-lpszURL)==0`）不开新标签；
  3. 脚本链接 `IS_SCRIPT_STR(lpszURL)` 不在此处处理（留给 `OnNewWindow2`）；
  4. `mailto:` 之类 `IS_MAILTO_STR` 排除。
- 新开流程：`NewChildWindow(0,1,NULL,this)` → `ToNavigate(lpszURL)` → `*pbCancel=TRUE`（`:693-711`），即新标签自建导航、原视图取消导航。
- `m_nInNewWindow` 由鼠标动作预置（`Src\MainFrm.cpp:15690` `ClickPoint`），用于把"用户点击意图"带到 `OnBeforeNavigate2`。
- 用户显式入口（右键菜单三态，`Src\GreenBrowserView.cpp:2542-2547`）：
  - 普通打开 → `INW_NORMAL`（0x1，受 `ActiveNew` 决定前后台）
  - 前台新标签 → `INW_ACTIVE`（0x2）
  - 后台新标签 → `INW_INACTIVE`（0x4）
  另有 `INW_OTHER_SIDE`（0x8）用于在另一侧打开。
- 重复 URL 去重：`g_bKillPop && g_bKillDupWin` 且距上次点击 < `POP_DELAY` 时，`FindDupURL` 命中则 `*pbCancel=TRUE` 并 `MDIActivate` 那个已存在标签（`:657-671`）。`KillDupWin` 默认 1（`Src\GreenBrowser.cpp:493`）。

### 现有 DualPanelBrowser 现状

- `_self` 链接在当前 WebView2 内导航，行为正确。
- `NewWindowRequested` 一律 `e.Handled = true` 并 `AddTab(e.Uri)`（`src\BrowserPane.xaml.cs:348-353`）。
- 没有"链接一律新标签"开关，没有右键"前台/后台/另一侧"三态，没有重复 URL 去重。

### 路线 B 设计

- 保留 WebView2 原生分流：普通链接当前页，`NewWindowRequested` 走新标签。
- 已存在同 URL 标签时（大小写与尾部 `/` 归一后比较），不发新标签，直接激活既有标签 —— 对齐 `FindDupURL`。
- "链接一律在新标签打开"是**反向**需求：WebView2 不会对 `_self` 给新窗口事件，需注入 JS 捕获 `click`（`button==0` 且无修饰键、`href` 非 `#`/`javascript:`/`mailto:`），`preventDefault()` 后经 `postMessage` 请求宿主开新标签。此项实现成本高于前两项，建议排在后面。
- ⚠️ 已核实：`NewWindowRequestedEventArgs.IsUserInitiated` 可用，但无修饰键信息；Ctrl/⌘ 判断改由页面内 `mousedown` 上报实现（见 2 节）。

#### 已实现：重复 URL 去重 ✅

- 代码：`src\BrowserPane.xaml.cs` `TryActivateExisting` / `NormalizeUrl`，`AddTab(..., dedup: true, ...)` 时先查重。
- 归一化规则：scheme/host 小写、非默认端口保留、路径去尾斜杠、保留 query、忽略 fragment；`about:blank`、`data:`、扩展管理页不参与去重。
- 作用范围：只对网页新链接通道（`NewWindowRequested`）启用。手动"新建标签"和地址栏输入同一 URL 不去重，保留"故意开第二个标签"的可能。
- 验证：CDP 在 36氪页执行 `window.open('https://www.ithome.com/')` → 日志 `dedup activate tab url=https://www.ithome.com/`，CDP 目标数保持 5，未新建 WebView。


---

## 2. 是否支持后台打开

### GreenBrowser 源码事实

- 开关 `m_bActiveNewWindow`，配置项 `ActiveNew`，**默认 0 = 后台打开**（`Src\GreenBrowser.cpp:540`）。
- 统一判据宏 `Src\GreenBrowserView.cpp:30`：
  ```
  #define CAN_ACTIVE(tcf) (!(tcf->m_nActive & INW_INACTIVE) && (pmf->m_bActiveNewWindow || tcf->m_bForceActive))
  ```
  即：只有被显式标记 `INW_INACTIVE`，或 `ActiveNew`/`ForceActive` 为真时才前台激活。
- 标志位定义 `Src\MainFrm.h:116-119`：`INW_NORMAL=0x1`、`INW_ACTIVE=0x2`、`INW_INACTIVE=0x4`、`INW_OTHER_SIDE=0x8`。
- 建窗后写回活动态：`Src\GreenBrowserView.cpp:701-704`
  ```
  tcf->m_nActive    = m_nInNewWindow;
  tcf->m_bForceActive = m_nInNewWindow & INW_ACTIVE;
  tcf->m_pView->m_bIsActive = (CAN_ACTIVE(tcf) ? TRUE : FALSE);
  ```
- `m_bIsActive` 影响：是否回写地址栏（`:412-435`）、是否更新 tab 图标（`:442-468`）；非活动标签用 `TI_100` 静默完成态图标（`:464-465`）。
- 拖拽打开也有前台/后台两种入口（`Src\MainFrm.cpp:15771-15772` 临时把 `m_bActiveNewWindow` 置 0 再恢复）。

### 现有 DualPanelBrowser 现状

- `AddTab` 结尾固定 `Tabs.SelectedItem = tab`（`src\BrowserPane.xaml.cs:302`），新标签必然前台。
- `SelectionChanged` 后对空白标签做 Input 级聚焦并全选地址栏（用于"新标签聚焦地址栏"特性），后台标签一旦被选中就会抢焦点。
- 已有后台预加载与 `_pending` / `_inited` 机制，可复用为后台标签的 WebView2 初始化路径。

### 路线 B 设计

1. `AddTab(string url, bool activate = true, int? insertIndex = null)`：`activate=false` 时只 `Tabs.Items.Insert`，不动 `SelectedItem`。
2. 后台标签的 WebView2 仍挂进 `ViewHost`（`Visibility=Collapsed` 或 `Opacity=0` 且 `IsHitTestVisible=false`），保证导航照常进行、`DocumentTitleChanged` 照常更新标题。
3. 抑制副作用：`SelectionChanged` 里判断"是否由用户操作触发"（用 `_suppressFocus` 标志包围程序化选中），避免后台标签激活时地址栏抢焦点。
4. 设置项"新标签打开后立即激活"，默认**关**（对齐 `ActiveNew=0`）。
5. 右键菜单：`打开链接` / `在新标签前台打开` / `在新标签后台打开` / `在另一侧面板打开`，映射到 `activate` 与目标 pane。
6. 后台标签不参与 `StatusLeft` 的扩展加载计时显示，避免状态栏闪烁。

#### 已实现：后台标签 ✅

- 代码：`src\BrowserPane.xaml.cs` `AddTab(url, activate, insertIndex, dedup, origin)`；`activate=false` 时不设置 `Tabs.SelectedItem`、不写 `AddressBox`、不聚焦地址栏，改为在 `DispatcherPriority.Background` 里异步 `InitAsync`，导航照常进行。
- 修饰键：`InitAsync` 注入 `mousedown` 监听，`postMessage('__mods__|ctrl|shift|meta')` 上报，宿主存 `_lastMods[WebView2]`；`NewWindowRequested` 时 Ctrl/⌘ 为真则强制后台（对齐 Chromium 惯例）。
- 设置项：`ActivateNewTab`（默认 `true`，保持原手感），菜单可切，写入 `session.json` 的 `ActivateNewTab` 字段。
- 验证：`ActivateNewTab=false` 时 CDP 触发 `window.open('https://example.com/')` → 日志 `AddTab url=https://example.com/ activate=False at=3 count=4` → `InitBackground` → `InitAsync` → `Navigate` → `NavCompleted`，全程无 `FocusWebView`，UIA 读数显示选中项仍是原标签。


---

## 3. 后台加载失败是否有重试

### GreenBrowser 源码事实（结论：没有自动重试）

- 全源码检索 `NavigateError` / 自动 `Refresh` / `Reload` 重试逻辑：**未命中**任何"失败后自动重试"实现。
- 导航状态只做 UI 反馈：
  - `m_nProgress == -10` 表示文档完成（`Src\GreenBrowserView.cpp:411`，由 `m_pDisp == NULL` 判定）；
  - `m_bFirst` / `m_bForceRefresh` 在首次导航后复位（`:720-721`）；
  - 失败时保留 IE 自身错误页，不做处理。
- 唯一相关项 `m_bReloadUnShownImage`（配置 `ReloadUnShownImage`，默认 TRUE）：后台未显示标签的**图片懒加载重载**，不是页面失败重试。
- 空白标签复用 `g_bFilteBlank`（默认 FALSE，`Src\GreenBrowser.cpp:603`）：`Src\MainFrm.cpp:6287-6314` 先复用 `m_pBlankChild`（URL 为空或 `about:blank`，或存在时间 < 0.25 秒），再由 `:6317-6344` 遍历复用 `about:blank` 标签，命中置 `m_bReUsed = TRUE`。
- ⚠️ 待核实：`OnProgressChange` / `DocumentComplete` 中是否存在"进度长时间不动即判失败"的定时器逻辑（`STOP_TIMER 950` 相关代码段尚未逐行核完）。

### 现有 DualPanelBrowser 现状

- `NavigationCompleted` 只做进度条收尾、地址回写、注入 `__focus__` 监听（`src\BrowserPane.xaml.cs:394-430`），**未读取 `IsSuccess` / `WebErrorStatus`**。
- 崩溃防御：`ProcessFailed` 把 WebView2 加入 `_dead` 集合，`CloseTab` 与回调里 catch `ObjectDisposedException / InvalidOperationException`，但**崩溃标签留在原位、需手动关**。

### 路线 B 设计（新增能力，非照搬）

1. 失败态可视：`NavigationCompleted` 判 `IsSuccess == false`，标签标题旁显示失败标记，`StatusLeft` 显示 `WebErrorStatus` 文本（如 `ConnectionAborted`）。
2. 重试策略（建议分档，需用户拍板）：
   - 后台标签：失败自动重试 N=2 次，间隔 1s / 3s；仍失败则停在失败态等用户操作；
   - 前台标签：**不自动重试**，避免打断用户正在看的错误页（与 GreenBrowser 保守行为一致）；
   - 仅对瞬时错误重试（`ConnectionAborted`、`Timeout`、`ServerUnreachable`），对 `HostNameNotResolved`、`HttpStatus4xx` 不重试。
3. 崩溃恢复：`ProcessFailed` 后提供"重建此标签并重放 URL"（新 `WebView2` 替换同 `TabItem`，复用 `_tabUrls` 中的 URL），而不是留死标签。

#### 已核实：Chromium/WebView2 底层自带的重试范围

结论：**连接层有重试，页面层没有**。因此路线 B 不重写网络重试，只补"页面级失败态与可选重试"。

- 连接复用被 `RST`/`Closed`/`Aborted`、响应完全为空、且尚未收到响应头时，Chromium 自动重发一次请求（`HttpNetworkTransaction` 的连接级重试）。
- 421 Misdirected Request、HTTP/2 GOAWAY、QUIC 协议错误有有限重试；Happy Eyeballs 在传输层对多个地址并行尝试。
- **不会**因为 4xx/5xx 或 `421/503` 的 HTTP 语义自动重发请求；代理 502 也不触发链式切换。
- 页面级"网络恢复后自动重载错误页"属于 Chromium 浏览器 UI 层的错误页脚本，WebView2 未继承，故这项工作仍要在宿主里做。
- 副作用：WebView2 的 `NavigationCompleted.IsSuccess == false` 只表示"落到了错误页"，可能包含用户主动取消（`OPERATION_CANCELED`），实现失败态时必须同时看 `WebErrorStatus` 区分。


---

## 4. 关闭新标签后返回原页面

### GreenBrowser 源码事实

- 关闭顺序 `m_nCloseSequence`，配置项 `CloseSequence`，**默认 2**（`Src\MainFrm.cpp:11426`）；注释 `Src\MainFrm.h:516`：`0-default, 1-next, 2-pre`。
- `CChildFrame::OnClose`（`Src\ChildFrm.cpp:122-244`）完整顺序：
  1. `m_bLock && g_bConfirmCloseLock` 时先弹确认（`:129-133`）；
  2. `m_bKeepOnePage && cn==1` 且当前是 `about:blank` 则拒绝关闭（`:136-140`）；
  3. 不可见（被弹窗过滤掉的）标签 `nTabID = -1`，此时不改当前标签（`:142-143`、`:195-197`）；
  4. `m_wndTab.DeleteItem(nTabID)`（`:152`）；
  5. 撤销缓冲：非 `about:blank` 的 URL 与标题写入环形数组 `m_strUndoURL / m_strUndoName`，`m_nUndoEnd` 前进、`MAX_UNDO` 回绕、`UpdateUndoMenu()`（`:161-171`）；
  6. 关闭顺序计算（`:197-216`）：
     - 仅当被关标签 `m_bIsActive` 或 `m_nDupID>=0` 才重选；
     - `m_nDupID>=0 && m_bActiveNewWindow` → 回到**源标签** `g_nNextTab = m_nDupID`；
     - `CloseSequence==0` → **末尾** `cn-1`；
     - `CloseSequence==1` → **右邻**（删除后同索引即原右邻）；
     - `CloseSequence==2` → **左邻** `nTabID-1`，为 0 时取 0；
  7. `MDIActivate(tcf)` + `m_wndTab.SetCurSel(nTabID)` 落地（`:227-231`）；
  8. 最后一个标签关闭时清空地址栏与进度（`:187-192`）。
- 相关开关默认值：`KeepOnePage=FALSE`（`Src\GreenBrowser.cpp:335`）、`AutoTab=1`（`:567`，标签栏自动适配宽度）。

### 现有 DualPanelBrowser 现状

- `CloseTab`（`src\BrowserPane.xaml.cs:617-648`）：`Tabs.Items.Count <= 1` 时走"清空当前标签内容"分支（`NavigateToString("<html></html>")` + 清空 `AddressBox/TitleLabel`），否则 `Tabs.Items.Remove(tab)` 后由 WPF `TabControl` 自行决定选中项。
- 结果：删中间标签时 WPF 默认选**右邻**；删末尾选左邻；与 GreenBrowser 默认"左邻"不一致，且无法配置。
- 没有来源标签概念（跨面板拖拽后 `AdoptTab` 也不记录来源），没有关闭撤销栈。

### 路线 B 设计

1. 关闭前记录：`(index, sourceTab, sourcePane)`。`AdoptTab` / 跨面板拖拽 / 弹窗派生时把来源写入 `_tabOrigin[tab]`。
2. 关闭后显式计算目标并 `Tabs.SelectedItem = target`，规则与 GreenBrowser 一致：
   - 有来源标签且来源仍存在 → 回来源；
   - 否则按设置：`pre`（默认，`index-1`，下限 0）/ `next`（`index`，上限 `Count-1`）/ `last`（`Count-1`）。
3. 设置项 `关闭标签后切换：左侧 / 右侧 / 最后一个`，默认左侧。
4. 撤销关闭：环形栈 `MAX_UNDO`（建议 20），保留 URL + 标题 + 原索引；`Ctrl+Shift+T` 与右键"恢复关闭的标签"恢复，恢复位置按原索引（超出则追加末尾）。
5. 焦点归属：关闭后焦点给新选中标签的**页面**（`wv.Focus()`），不抢地址栏；`CloseTab` 的 `SelectionChanged` 需抑制地址栏聚焦。
6. 最后一个标签：保持现状（清空内容而非关窗）作为默认，与 `KeepOnePage` 语义对齐；可加设置项允许关到底后自动开新空白标签（对应 `KeepOnePage` 开启时 `ID_FILE_NEW_BLANK` 的行为，`Src\ChildFrm.cpp:241-242`）。

#### 已实现：关闭回退（来源标签优先 + 可配顺序）✅

- 代码：`src\BrowserPane.xaml.cs` `CloseTab` + `ResolveCloseTarget`；`_tabOrigin` 记录派生来源（`NewWindowRequested` 时传入 `origin`），`DetachTab` / `AdoptTab` 跨面板时清除。
- 顺序：`CloseBehavior.Previous`（默认，左邻）/ `Next` / `Last`，由 `CloseSequence` 属性控制，菜单三选一，写入 `session.json`。
- 只在被关标签是当前选中时重选；关闭后台标签不动当前页。
- 附带修复：`CloseTab` 原实现 `_tabUrls.Remove(wv!)` 在标签已不在 `_tabWv` 时会以 null 为键抛 `ArgumentNullException`，改为先 `TryGetValue` 守卫并提前返回。
- 验证：
  - 左邻：`CloseTab entry idx=1 count=2 current=True` → `CloseTab reselect idx=0 origin=no`。
  - 来源优先：从第 1 个标签派生新标签（idx=2）后关闭 → `CloseTab reselect idx=0 origin=yes`，未按左邻去 idx=1。


---

## 5. 标签生命周期状态机（路线 B 目标）

```
[创建] AddTab(url, activate, insertIndex, source?)
   ├─ activate=false  → [后台加载中]  (不可见/不抢焦点, 进度与标题仍更新)
   └─ activate=true   → [前台加载中]
        [后台加载中] --用户点击标签--> [前台加载中]

[前台加载中] --NavigationCompleted(IsSuccess)--> [前台已加载]
[前台加载中] --NavigationCompleted(!IsSuccess)--> [前台失败态]  (等用户刷新)
[后台加载中] --NavigationCompleted(IsSuccess)--> [后台已加载]
[后台加载中] --NavigationCompleted(!IsSuccess)--> [后台重试(1/2)] → 仍失败 → [后台失败态]

[任意] --ProcessFailed--> [失效态] (_dead)
   ├─ 关闭 → 清理并回退
   └─ 重建 → 同 TabItem 换新 WebView2 重放 URL → [前台/后台加载中]

[关闭] CloseTab
   ├─ 有来源标签且存在 → 选中来源
   ├─ 否则按顺序设置 → 左邻 / 右邻 / 末尾
   ├─ 写入撤销栈（URL+标题+索引）
   └─ 仅剩 1 个 → 清空内容（或按设置自动补空白标签）
```

---

## 6. 落地优先级与验收口径

### 第一批（✅ 已完成）

1. **后台标签** ✅：`AddTab(url, activate)`，Ctrl/⌘+点击后台打开，菜单可切默认前后台。
   - 验收（已通过）：后台打开时当前标签不变、地址栏文本不变；新标签在标签栏出现、标题随后更新、页面已导航完成。
2. **关闭回退左邻 + 来源标签** ✅：替换 WPF 默认选中，顺序可配。
   - 验收（已通过）：从 A 派生 D，关闭 D 回到 A；无来源时 `[A,B,C]` 关 B 选 A、关 C 选 B。
3. **重复 URL 去重** ✅：同 URL 已有标签时激活而非新开，限网页新链接通道。
   - 验收（已通过）：连点两次同一链接只新增一个标签；再次请求切到已存在标签。

### 第二批（⬜ 未开工）

4. 链接落点全局开关（需 JS 注入拦截 `click`）。
5. 关闭撤销栈（`Ctrl+Shift+T`）。
6. 失败态标记 + 后台瞬时错误重试。
7. 空白标签复用（`about:blank` 优先承接新链接）。
8. 崩溃标签"重建并重放 URL"。

### 明确不做（保留 GreenBrowser 保守行为）

- 前台标签失败自动重试；
- 关闭最后一个标签时直接关窗（默认维持清空内容）。

---

## 7. 待确认清单（需用户拍板）

1. 后台打开是否作为默认行为（对齐 GreenBrowser `ActiveNew=0`）。**当前实现默认前台**，设置项已就绪，改默认值只需一行。
2. 关闭顺序默认取"左邻"（对齐 GreenBrowser）还是"右邻"（对齐现代浏览器/Chrome）。**当前实现默认左邻**，可菜单切换。
3. 后台标签失败重试次数与间隔（建议 2 次 / 1s·3s）。未实现。
4. 是否要"链接一律新标签打开"的全局开关（实现成本最高，且会改变现有手感）。未实现。
5. 标签上限：是否设硬上限（如 30），避免后台标签过多占用内存。**当前无上限**。

---

## 附：GreenBrowser 关键位置索引

| 行为 | 文件:行 |
|---|---|
| `CAN_ACTIVE` 判据宏 | `Src\GreenBrowserView.cpp:30` |
| 新标签分流条件 | `Src\GreenBrowserView.cpp:646` |
| 锚点/脚本/mailto 排除 | `Src\GreenBrowserView.cpp:681-716` |
| 新标签创建 + 活性写回 | `Src\GreenBrowserView.cpp:693-711` |
| 重复 URL 去重 | `Src\GreenBrowserView.cpp:657-671` |
| 右键三态开链接 | `Src\GreenBrowserView.cpp:2542-2547` |
| `OnNewWindow2` | `Src\GreenBrowserView.cpp:908` |
| `INW_*` 标志位定义 | `Src\MainFrm.h:116-119` |
| `CloseSequence` 注释 | `Src\MainFrm.h:516` |
| `NewChildWindow` 入口 | `Src\MainFrm.cpp:6252` |
| 空白标签复用 | `Src\MainFrm.cpp:6287-6344` |
| `LinkInNewWindow` 默认 0 | `Src\MainFrm.cpp:11340` |
| `CloseSequence` 默认 2 | `Src\MainFrm.cpp:11426` |
| `ActiveNewAddress` 默认 TRUE | `Src\MainFrm.cpp:11334` |
| `OnClose` 全流程 | `Src\ChildFrm.cpp:122-244` |
| 关闭顺序计算 | `Src\ChildFrm.cpp:197-216` |
| 撤销缓冲写入 | `Src\ChildFrm.cpp:161-171` |
| `ActiveNew` 默认 0 | `Src\GreenBrowser.cpp:540` |
| `FilteBlank` 默认 FALSE | `Src\GreenBrowser.cpp:603` |
| `KeepOnePage` 默认 FALSE | `Src\GreenBrowser.cpp:335` |
| 四类弹窗过滤默认 1 | `Src\GreenBrowser.cpp:491-494` |

## 附：现有 DualPanelBrowser 对应位置（按方法索引，避免行号漂移）

| 行为 | 位置 |
|---|---|
| 新建标签（含前后台/去重/来源参数） | `BrowserPane.xaml.cs` `AddTab(...)` |
| 新窗口请求分流与修饰键判断 | `BrowserPane.xaml.cs` `InitAsync` 内 `NewWindowRequested` |
| 修饰键上报脚本与消息解析 | `BrowserPane.xaml.cs` `InitAsync` 内 `ExecuteScriptAsync` / `WebMessageReceived` |
| 重复链接去重 | `BrowserPane.xaml.cs` `TryActivateExisting` / `NormalizeUrl` |
| 关闭标签与回退选目标 | `BrowserPane.xaml.cs` `CloseTab` / `ResolveCloseTarget` |
| 跨面板接管 / 移交 | `BrowserPane.xaml.cs` `AdoptTab` / `DetachTab` |
| 标签行为设置项与持久化 | `BrowserPane.xaml` 菜单 + `BrowserPane.xaml.cs` 三个属性；`MainWindow.xaml.cs` `SessionState` |
| 加载完成处理（未含失败态） | `BrowserPane.xaml.cs` `InitAsync` 内 `NavigationCompleted` |
