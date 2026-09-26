# Windows 端独立软件化（按计划书 Windows 客户端章节）

## Context

Windows 端当前是控制台程序：必须开终端、敲 `TrackLink.Windows.exe remote`、靠 Ctrl+C 退出，界面全靠滚动日志。用户要求「将 Windows 端写成独立软件」，并明确「按计划书做」。

计划书 `TrackLink-纯软件项目计划.md` 的 Windows 客户端章节已经把要求写死，但代码只兑现了其中一部分：

| 计划书要求 | 位置 | 现状 |
|---|---|---|
| UI 模块 `DevicePickerView` / `ConnectionView` / `SettingsView` | 第 67-82 行 | ❌ 完全没有 UI |
| 手动「连接、断开、暂停控制」按钮 | 第 47 行 | ❌ 无 |
| 首次运行让用户选内置触控板；显示厂商名、设备路径、最近活动时间；滑动 3 秒高亮确认；保存路径指纹、下次自动匹配、失败要求重选 | 第 84-95 行 | ⚠️ 只做了自动分组键匹配，无选择流程、无厂商名、无活动时间、无持久化 |
| 不支持仅触控板过滤时明确提示，不能猜测来源 | 第 95 行 | ⚠️ 只在控制台打一行日志 |
| 支持灵敏度、边缘加速、反向滚动、安全停止热键 | 第 111-118 行 | ⚠️ 灵敏度是 `const MoveGain = 2.5`，其余三项没有 |
| UI 框架 WPF（或 WinUI 3） | 第 61 行 | ❌ 控制台 |

本次把这一章全部落地。**改造难点不在并发架构**——现有代码已把重活放在自带的后台线程上（`RfcommHost` 的 `rfcomm-accept` 线程、`BtSession` 的 `rfcomm-heartbeat` 线程、`RawInputListener` 的 `rawinput-pump` 线程、`LocalMouseBlocker` 的钩子线程），主线程原本只做 `stopped.Wait()`，换成 WPF 消息泵即可，四者都不冲突。真正要处理的是 **WPF/XAML 工程配置、UI 线程编组、设备选择与持久化、托盪、退出兜底、打包**。

## 已确认的决策

| 决策点 | 选择 |
|---|---|
| 本次范围 | **只做计划书的 Windows 客户端章节**；第 5 周（会话认证/重放保护/后台重连）与第 6 周（首次使用向导/诊断报告/隐私说明）留待后续 |
| 托盘/关闭行为 | 带托盘；关窗口 = 最小化到托盘，服务与手势转发继续在后台生效；托盘「退出」才真正结束 |
| 分发形式 | 自包含单文件 exe（免装 .NET 运行时，拷走即用） |
| 命令行模式 | 保留 `selftest` / `serve` / `remote`，GUI 为无参数默认 |
| 界面组织 | 一个主窗口内承载三个视图（左侧导航切换），文件名与计划书的 `UI/DevicePickerView` / `ConnectionView` / `SettingsView` 对齐 |

## 关键约束与已核实结论

- **必须 WPF，不能用 WinUI 3**：本机不装 Visual Studio / Windows SDK，只用 `dotnet build`；8 GB 内存。
- **编译链已够用**：本机已有 `Microsoft.WindowsDesktop.App.Ref/8.0.31`、`Sdks\Microsoft.NET.Sdk.WindowsDesktop`、共享运行时 `PresentationNative_cor3.dll` / `System.Windows.Forms.dll` / `System.Drawing.Common.dll` —— **无需 Windows SDK、无需任何 NuGet 包**。
- **`--self-contained` 首次发布需联网**：NuGet 缓存里没有 `Microsoft.WindowsDesktop.App.Runtime.win-x64`；网络不可用时退回框架依赖发布。
- 构建命令固定：`& "$env:USERPROFILE\.dotnet\dotnet.exe" build "d:\Code\Projects\TrackLink\TrackLink.Windows\TrackLink.Windows.csproj" -c Release`，必须 0 警告 0 错误。
- 代码注释与界面文案一律**简体中文**。
- 发布输出目录用 `d:\Code\Projects\TrackLink\publish\`（沙箱可写）。

## 工程配置

改 `TrackLink.Windows\TrackLink.Windows.csproj`：

```xml
<OutputType>WinExe</OutputType>          <!-- 去掉黑窗口；代价：Console 默认变空流 -->
<UseWPF>true</UseWPF>
<UseWindowsForms>true</UseWindowsForms>  <!-- 仅为 NotifyIcon / SystemIcons；可与 UseWPF 共存 -->
<!-- 删除 <InvariantGlobalization>true</InvariantGlobalization> -->
```

- **删除 `InvariantGlobalization`**：WPF 下 `XmlLanguage` / `CultureInfo` 名称解析在 invariant 模式会退化甚至抛 `CultureNotFoundException`，收益仅 2~3 MB，不值得冒险。保留 `SatelliteResourceLanguages=en`。
- **不要在 csproj 里写 `RuntimeIdentifier` / `SelfContained` / `PublishSingleFile`**：会让日常 `dotnet build` 也去拉运行时包。RID 只在 `publish` 命令行传。
- **跳过 `ApplicationIcon`**（项目没有 .ico）；窗口与托盘图标用运行时 `SystemIcons`。**不生成图像资源**。
- 命名冲突：加 `using WinForms = System.Windows.Forms;`，全限定 `Application` / `MessageBox` / `Clipboard`。

## 文件改动

### 新增

| 文件 | 职责 |
|---|---|
| `App.cs` | `: Application`；`ShutdownMode=OnMainWindowClose`；挂 `DispatcherUnhandledException` 兜底清理 |
| `MainWindow.xaml` / `.cs` | 主窗口外壳：左侧导航 + 三个视图容器、托盘、全局热键、单实例唤醒、`DispatcherTimer` 轮询 |
| `UI/DevicePickerView.xaml` / `.cs` | 计划书第 84-95 行：候选触控板列表（厂商名 / 产品名 / 设备路径 / 分组键 / 最近活动时间）、「开始 3 秒检测」高亮确认、「就用它」、匹配失败时的重选提示、「不支持仅触控板过滤」提示 |
| `UI/ConnectionView.xaml` / `.cs` | 计划书第 47 行：服务状态（本机地址/通道/SDP）、连接状态与远端地址、实时 RTT、最近手势、滚动日志、「启动服务 / 停止服务 / 断开当前会话 / 暂停控制」四个按钮 |
| `UI/SettingsView.xaml` / `.cs` | 计划书第 111-118 行：灵敏度、边缘加速（开关 + 系数）、反向滚动（开关）、安全停止热键 |
| `UI/RemoteEngine.cs` | 编排 `RfcommHost` + `RawInputListener` + `GestureRecognizer` + `LocalMouseBlocker`；`Start()` / `Stop()` / 快照属性 / 暂停控制 / 手动断开；`IDisposable` |
| `UI/UiLogSink.cs` | `Dispatcher` 编组 + 定长环形日志缓冲（上限 500 行） |
| `UI/ConsoleMode.cs` | 现有 `RunSelfTest` / `RunServer` / `RunRemote` / `PrintUsage` / `SendCommand` **原样搬迁**，`Console.WriteLine` 换成 sink 委托 |
| `UI/TrayIcon.cs` | `NotifyIcon` + `ContextMenuStrip` 封装 |
| `Settings/AppSettings.cs` | 配置模型 + `%LOCALAPPDATA%\TrackLink\settings.json` 的读写（`System.Text.Json`，in-box，无需 NuGet）：触控板指纹（厂商/产品/分组键/路径）+ 四项手势设置 + 暂停态不持久化 |
| `Settings/GestureOptions.cs` | 可调手势参数：`MoveGain`、`EdgeAccelEnabled`、`EdgeAccelFactor`、`EdgeZoneRatio`、`ReverseScroll`、`StopHotkey` |

### 修改

| 文件 | 改动 |
|---|---|
| `Program.cs` | 入口改为 `[STAThread] static int Main`：`args.Length == 0` → GUI；否则走控制台分支（先 `AttachConsole`） |
| `Input/TouchpadCatalog.cs` | `TouchpadDevice` 加 `Manufacturer` / `ProductName`；新增 `GetHidStrings(path)`——`CreateFile` 打开设备后调 `hid.dll` 的 `HidD_GetManufacturerString` / `HidD_GetProductString`，失败回退显示 `VID:PID` |
| `Input/RawInputListener.cs` | ① 按设备句柄记录最近活动时刻（`Dictionary<IntPtr, long>` + `DeviceActivityMs(handle)` 快照），即使在未选定触控板时也记录，供选择页高亮；② 加 `SelectedGroupKey` 过滤——为空时只做活动检测、**不**触发 `Report`，选定后只转发该分组键的报文 |
| `Input/GestureRecognizer.cs` | 常量改为读 `GestureOptions` 快照：`MoveGain` 可调；新增边缘加速（按归一位置判边缘区，位移乘系数）与反向滚动（Scroll 的 dy 取反） |
| `Bluetooth/BtSession.cs` | 新增 `public event Action<long>? Rtt;`，在 `Pong` 分支（第 91-101 行）`Rtt?.Invoke(rtt)` |

### 基本不动

`Input/RemoteCommand.cs`、`RawInputNative.cs`、`HidTouchpadDecoder.cs`、`LocalMouseBlocker.cs`、`Bluetooth/` 其余文件（`RfcommHost` / `Protocol` / `BtSockets` / `SdpRecordBuilder`）、`Protocol` 帧格式。**这是最大的回归保障**。

> 命名说明：计划书模块表里的 `DeviceCatalog.cs` / `TouchpadFilter.cs` 职责已由现有的 `TouchpadCatalog.cs` / `RawInputListener.cs` 承担，不为了对齐名字而重命名（重命名只增加回归风险）。`SessionCrypto.cs` / `ProtocolWriter.cs` 属第 5 周，本次不做。

## 实现要点

### 入口与控制台模式

`WinExe` 下 `Console.Out` 是 `TextWriter.Null`，`Console.WriteLine` 静默丢弃。控制台模式需重建标准流：

```csharp
[DllImport("kernel32")] static extern bool AttachConsole(uint pid);
const uint ATTACH_PARENT = 0xFFFFFFFF;
if (!AttachConsole(ATTACH_PARENT)) AllocConsole();   // 无父终端（双击）时兜底
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
```

`AutoFlush` 必须有，否则看不到输出。`Console.CancelKeyPress` 与退出码（自检 0/2）在 attach 后照旧可用。

### 不建 App.xaml（重要）

`.xaml` 若以 `App.xaml` 命名会自动成为 `ApplicationDefinition`，XAML 编译器会生成带 `[STAThread] static Main` 的入口，与 `Program.cs` 的 Main 冲突（**CS0017**）。不建 App.xaml 时所有 `.xaml` 默认编译为 `Page`，WPF 默认主题仍自动生效。改为用代码定义 `App.cs`。

### 触控板选择与持久化（计划书 84-95）

- **候选项** = `TouchpadCatalog.Enumerate()` 里 `IsTouchpadCollection == true` 的条目（顶层集合 `0x0D/0x05`）。
- **3 秒确认**：点「开始检测」后进入 3 秒采样窗口，`DevicePickerView` 用 250 ms 定时器刷新每个候选的「最近活动时间」，**窗口内活动计数最大者高亮**；点「就用它」写入 `SelectedGroupKey`。
- **持久化**：把厂商名 / 产品名 / 分组键 / 设备路径写进 `settings.json`。
- **下次启动**：枚举后按「分组键相同」匹配 → 命中则直接进 ConnectionView（跳过选择页）；未命中则进 DevicePickerView 并提示「上次选的触控板未找到，请重新选择」。
- **不支持情形**：枚举结果里没有任何 `0x0D/0x05` 集合 → 界面明确显示「不支持仅触控板过滤」并禁用启动按钮，**不猜来源**。
- 诊断信息（VID:PID、归一化范围、范围来源）保留在 ConnectionView 显示。

### 三项设置与热键（计划书 111-118）

- **灵敏度**：`MoveGain` 从 `const` 改为 `GestureOptions` 字段（界面滑块，范围 0.5–5.0，默认 2.5）。
- **边缘加速**：手指位于触控板边缘 `EdgeZoneRatio`（默认 15%）内时，位移增量乘 `EdgeAccelFactor`（默认 1.5），可开关。
- **反向滚动**：`Scroll` 帧的 `dy` 取反，可开关。
- **安全停止热键**：`RegisterHotKey` 注册在 `MainWindow` 的 `HwndSource` 上（`WindowInteropHelper` + `AddHook`），默认 `Ctrl+Alt+Q`，触发即「暂停控制」（等价于 ConnectionView 的暂停按钮），再次按下恢复；退出时 `UnregisterHotKey`。

### 界面与数据来源

**ConnectionView**
- 服务状态：本机蓝牙地址、RFCOMM 通道、SDP 注册结果 ← `host.LocalAddress` / `host.Channel` / `host.ServiceRegistered`
- 连接状态：「等待连接」/「已连接 E0:DC:…」← `host.SessionChanged`；实时 RTT ← `BtSession.Rtt`；最近手势 ← `recognizer.Command`
- 按钮：「启动服务」/「停止服务」← `RemoteEngine.Start/Stop`；「断开当前会话」← `host.CurrentSession?.Dispose()`；「暂停控制」← 引擎内挡掉 `recognizer.Command`
- 日志面板：`ListBox` 绑定 `ObservableCollection<string>`（上限 500 行）+「自动滚动」+「清空」

**SettingsView**：四项设置 + 「恢复默认」；改动即时生效并写入 `settings.json`。

### 线程与数据流

- **离散事件**（`Log`、`SessionChanged`、命令发送记录）→ `UiLogSink.Post(line)`，内部 `Dispatcher.BeginInvoke(DispatcherPriority.Background, …)`，进入前判 `HasShutdownStarted` / `HasShutdownFinished`。
- **不要用 `SynchronizationContext.Current` 捕获**——后台线程上创建对象时拿到的是 null，容易踩坑。
- **连续状态**（RTT、会话、暂停态、屏蔽态、设备活动时间）→ 引擎写 `volatile` 字段 / 线程安全字典，UI 用 `DispatcherTimer` 250 ms 轮询快照。多数状态更新因此完全不需要编组。
- MOVE / DRAG_MOVE 已是静默帧（见 `Program.cs` 的 `SendCommand`），不产生日志；心跳 1 条/秒，量级极小，日志只需**限行数**，不必额外限流。

### RTT 暴露

不解析日志文本（格式随文案变动、要分配字符串做正则、丢精度、无法区分「RTT 变大」与「重连后首帧」）。直接加事件：

```csharp
public event Action<long>? Rtt;
// Pong 分支内：
var rtt = Environment.TickCount64 - sentAt;
Rtt?.Invoke(rtt);
```

UI 在 `SessionChanged != null` 时 `session.Rtt += OnRtt`，`null` 时解绑（用 `ReferenceEquals(host.CurrentSession, session)` 防竞态）。

### 生命周期与退出兜底（最关键）

`LocalMouseBlocker.Stop()` 必须被执行，否则系统鼠标钩子残留、光标行为异常。`RemoteEngine.Dispose()` 清理顺序：停监听 → 解绑手势事件 → `host.Dispose()` → `finally { LocalMouseBlocker.Stop(); }`。

三重兜底（`LocalMouseBlocker.Stop()` 幂等，重复调用安全）：

1. `MainWindow.Closed` → `engine.Dispose()`
2. `App.DispatcherUnhandledException` → `LocalMouseBlocker.Stop()` + `Shutdown(-1)`
3. `AppDomain.CurrentDomain.ProcessExit` / `UnhandledException` → `LocalMouseBlocker.Stop()`

三条路径：关窗口 → 隐藏到托盘（引擎继续跑，**不** Dispose）／托盘「退出」→ `Shutdown()` → 走 `Closed` 清理／最小化 → `StateChanged` → `Hide()`。

### 单实例（必要）

两个实例各自 `bind(BT_PORT_ANY)` 会拿到不同通道，却注册**同一 UUID 的 SDP 记录**，手机可能连到错误通道。用 `new Mutex(true, @"Local\TrackLink.Windows.SingleInstance", out var created)`；`!created` 时尝试 `FindWindow` + `ShowWindow`/`SetForegroundWindow` 唤起已运行实例，失败则提示后退出。

### 托盘图标

`UseWindowsForms` + `WinForms.NotifyIcon` + `ContextMenuStrip`（纯 P/Invoke `Shell_NotifyIcon` 要自己管 `NOTIFYICONDATA`、`WM_` 消息、`TaskbarCreated` 重建，代码量约 3 倍且无收益）。

- 必须在 **UI 线程**创建（NotifyIcon 内部隐藏窗口需要消息泵，WPF Dispatcher 提供）
- 菜单三项：显示主窗口 / 最小化到托盘 / 退出
- 退出前 `notifyIcon.Visible = false; notifyIcon.Dispose();`
- 图标用 `SystemIcons.Application`；窗口图标用 `Imaging.CreateBitmapSourceFromHIcon(SystemIcons.Application.Handle, …)` 并 `Freeze()`

### 发布为独立软件

```powershell
& "$env:USERPROFILE\.dotnet\dotnet.exe" publish "d:\Code\Projects\TrackLink\TrackLink.Windows\TrackLink.Windows.csproj" `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -o "d:\Code\Projects\TrackLink\publish\win-x64"
```

- `IncludeNativeLibrariesForSelfExtract=true` **必加**：WPF 的 `PresentationNative_cor3.dll` / `wpfgfx_cor3.dll` 是原生库，否则单文件运行时会缺 DLL。
- **绝不要加 `PublishTrimmed`**——WPF 不支持裁剪。
- **绝不要加 `EnableCompressionInSingleFile`**——本机实测它会让窗口客户区**全黑**：原生库确实被提取到 `%TEMP%\.net\TrackLink.Windows\`，进程不报错不崩溃、标题栏正常，但 WPF 渲染管线初始化失败。去掉压缩后同一份代码渲染完全正常。体积由约 63 MB 涨到约 138 MB，可接受。
- 预期产出单个 `TrackLink.Windows.exe`，约 138 MB。
- 网络不可用时退回框架依赖：去掉 `--self-contained` 与 `Include*` 参数，产出约 1.5 MB exe，依赖已装的 `Microsoft.WindowsDesktop.App 8.0.31`。

## 实施步骤

每步都可独立验证，前两步不碰底层代码。

1. **改 csproj**（`WinExe` + `UseWPF` + `UseWindowsForms`，删 `InvariantGlobalization`）。
   验证：原构建命令 0 警告 0 错误；`bin\Release\net8.0-windows\TrackLink.Windows.exe selftest` 输出为**空**（证实 WinExe 吞掉控制台），`$LASTEXITCODE` 仍为 0/2。
2. **加 `AttachConsole` 重建标准流 + 新建 `UI/ConsoleMode.cs`**（搬迁四个控制台方法，`Input/`、`Bluetooth/` 一行未动）。
   验证：`selftest` 三步自检文案完整可见、`$LASTEXITCODE -eq 0`；`serve` / `remote` 照旧可跑。
3. **加 `Settings/AppSettings.cs` + `Settings/GestureOptions.cs`**（读写 `settings.json`，默认值=现有常量）。
   验证：构建 0/0；控制台 `remote` 仍按原灵敏度工作；手工改 json 后重启，值被读回。
4. **加最小 `MainWindow` + `App.cs` + `Program.Main` 的 GUI 分支**。
   验证：构建 0/0；无参运行出窗口；关闭窗口进程退出、鼠标行为正常。
5. **加 `UiLogSink` + `RemoteEngine`**，把 remote 模式接线搬进引擎。
   验证：GUI 内启动服务、手机连接后日志面板出现「[连接] 远端设备 …」与心跳日志；「暂停控制」勾上后手机不响应手势，取消后恢复。
6. **补齐 `ConnectionView`**（状态 / RTT / 四个按钮 / 日志限行）+ `BtSession` 的 `Rtt` 事件。
   验证：界面 RTT 与日志 `[心跳] PONG 往返 N ms` 数值一致（±1 个心跳）。
7. **`TouchpadCatalog` 加厂商名/产品名 + `RawInputListener` 加设备活动时间与 `SelectedGroupKey` 过滤 + 实现 `DevicePickerView`（3 秒高亮、持久化、下次自动匹配、不支持提示）**。
   验证：首启进选择页；滑动触控板 3 秒后对应设备高亮且「最近活动」刷新；选定后重启程序不再询问；手工改坏 json 里的分组键后重启，回到选择页并给出重选提示。
8. **`GestureRecognizer` 接入 `GestureOptions`（灵敏度/边缘加速/反向滚动）+ 实现 `SettingsView` + 全局热键**。
   验证：滑块拉大后手机光标跟手更快；开边缘加速后在触控板边缘滑动明显更远；开反向滚动后双指向上滑变成向下滚；按 `Ctrl+Alt+Q` 立刻停止控制，再按恢复；重启后设置保持。
9. **托盘 + 最小化到托盘 + 单实例 + 三重退出兜底**。
   验证：最小化后窗口消失、托盘双击还原；托盘退出后任务管理器无 `TrackLink.Windows`；连续开关 10 次「屏蔽本机鼠标」后外接鼠标移动与点击均正常。
10. **自包含单文件发布 + 端到端验证**。

## 端到端验证

1. 双击 `publish\win-x64\TrackLink.Windows.exe` → 窗口出现、托盘有图标、任务管理器有进程；首启落到 DevicePickerView。
2. 滑动触控板 3 秒 → 目标设备高亮 → 「就用它」→ 进入 ConnectionView，显示「本机地址 / 通道 / SDP 注册成功 / 触控板范围与来源」。
3. 手机连接 TrackLink 并启用无障碍服务 → 界面显示「已连接 + 远端地址」，RTT 每秒刷新。
4. 真机回归手势：单指移动/轻点、双击后按住拖动、双指滚动、三指横滑返回、三指轻点回桌面。
5. 四项设置逐项生效；`Ctrl+Alt+Q` 立即停止、再按恢复。
6. 「屏蔽本机鼠标」勾上后本机光标不跟手、外接鼠标照常；取消后本机光标恢复。
7. 关窗口 → 窗口消失但手势仍生效（托盘在）；托盘「退出」→ 进程消失、三指手势与本机光标行为恢复正常。
8. `TrackLink.Windows.exe selftest` 在终端里仍输出三步结论。

## 风险与规避

| 风险 | 规避 |
|---|---|
| `InvariantGlobalization` 触发 WPF 区域性异常 | 删除该属性 |
| `App.xaml` 自动生成 Main → CS0017 | 不建 App.xaml，用代码定义 `App.cs` |
| `Application` / `MessageBox` 在 WPF 与 WinForms 间命名歧义 | `using WinForms = System.Windows.Forms;` 全限定 |
| `WinExe` 后 `Console.WriteLine` 静默失效 | 重建标准流 + `AutoFlush`；`selftest` 必须实测可见 |
| 缺 `[STAThread]` | 自写 Main 上显式标注，否则运行时抛「调用线程必须是 STA」 |
| `HidD_GetManufacturerString` 取不到字符串 | 回退显示 `VID:PID`，选择页仍可用（靠 GroupKey 唯一标识） |
| 应用设置改动后 `GestureRecognizer` 仍用旧值 | 设置改动走「写 options → 重建或用 volatile 快照读取」；手势线程每帧读快照，不缓存副本 |
| 全局热键与别的程序冲突 | `RegisterHotKey` 失败时界面提示并在设置页显示「热键未生效」，不静默失败 |
| 单文件发布缺原生库 | `IncludeNativeLibrariesForSelfExtract=true` |
| 单文件开启压缩后窗口全黑 | **不用 `EnableCompressionInSingleFile`**（已实测，见上） |
| 裁剪破坏 WPF | 不使用 `PublishTrimmed` |
| NotifyIcon 线程/消息泵错误 | 在 UI 线程创建，退出前 Dispose |
| UI 线程阻塞导致手势卡顿 | 重活全在既有后台线程；UI 只做 `BeginInvoke(Background)` + 250 ms 轮询；日志限 500 行 |
| 双击 exe 跑 `selftest` 看不到输出 | `AllocConsole()` 兜底 |
| 双实例抢 SDP 记录 | 单实例 Mutex |
| 首次 `publish` 需联网 | 备好框架依赖回退命令 |

## 本次不做（计划书其他章节，后续轮次）

- 第 5 周：会话认证与重放保护（`SessionCrypto`）、压测/功耗/后台自动重连、多品牌覆盖
- 第 6 周：首次使用向导、诊断报告导出、隐私与无障碍用途说明、兼容设备清单
- 第 2 周遗留：已配对设备自动重连（计划书已注明放到第 5 周）
- 已知且用户已接受：Windows 自带的三指手势仍会在本机触发系统动作（触控板 PTP 栈不经鼠标消息管道，用户态无法可逆屏蔽）

## 需要回写计划书

- 第 61 行框架选型落定为 **WPF**（放弃 WinUI 3；理由：不装 VS/Windows SDK、纯 `dotnet build`）
- 第 67-82 行模块表补上实际落地文件：`UI/RemoteEngine.cs`、`UI/UiLogSink.cs`、`UI/ConsoleMode.cs`、`UI/TrayIcon.cs`、`Settings/AppSettings.cs`、`Settings/GestureOptions.cs`；并注明 `DeviceCatalog` / `TouchpadFilter` 职责由 `TouchpadCatalog` / `RawInputListener` 承担
- 新增一节记录本轮偏离与补充：托盘常驻与关闭行为、自包含单文件发布、保留命令行模式、`LocalMouseBlocker`（计划书未提及的本机鼠标屏蔽机制）