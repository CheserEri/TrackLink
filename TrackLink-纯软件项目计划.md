# TrackLink：笔记本触控板远程操控 Android（纯软件）项目计划

## 项目定义

### 目标

- 将笔记本内置触控板的输入，通过蓝牙发送到 Android 手机。
- 手机端把指令转换为点击、滑动及系统级返回、主页、最近任务等操作。
- 仅转发触控板输入，不转发外接鼠标。
- 不依赖局域网、云服务或外接硬件。

### 产品边界

这是“触控板远程操控 Android”，而非 Android 系统级的真实蓝牙 HID 鼠标。Windows 普通应用难以把内置蓝牙直接作为 HID 鼠标外设；本项目改用 RFCOMM 自定义通信，并由 Android 无障碍服务执行操作。

不包含以下能力：

- 不模拟 Android 系统级硬件鼠标指针。
- 不绕过锁屏、支付、系统授权弹窗或应用安全限制。
- 不采集、上传或云端存储输入内容。

## 总体架构

```text
┌───────────────── Windows 笔记本 ─────────────────┐
│ 触控板 → Raw Input 设备筛选 → 手势识别/坐标映射   │
│           → 指令编码 → RFCOMM 蓝牙连接             │
└───────────────────────────┬───────────────────────┘
                            │ 已配对的加密蓝牙链路
┌───────────────────────────▼───────────────────────┐
│ Android 手机                                        │
│ RFCOMM 接收服务 → 协议校验 → 操作调度器             │
│                         ├─ 无障碍点击/滑动          │
│                         └─ 全局返回/主页/最近任务   │
└───────────────────────────────────────────────────┘
```

传输选用 Bluetooth Classic RFCOMM：它是双向、面向连接的字节流，Windows 和 Android 均提供公开 API。建议 Windows 端作为服务端广播 RFCOMM 服务，Android 端从已配对设备列表连接。

## MVP 范围

- 单指移动：转换为手机上的短滑动。
- 触控板轻触或物理左键：单击。
- 双击：双击。
- 长按：长按。
- 两指上下滑：竖向滚动。
- 三指向左/右滑：返回/最近任务。
- 手动“连接、断开、暂停控制”按钮。

### MVP 验收标准

- 已连接状态连续使用 10 分钟不掉线。
- 点击成功率不低于 95%。
- 从笔记本输入到手机开始执行的中位延迟低于 100 ms。
- 外接鼠标的移动、点击不会影响手机。

## Windows 客户端

### 技术选型

- C# + .NET 8 或更高版本。
- UI：**WPF**（`net8.0-windows` + `UseWPF`，已落地）。放弃 WinUI 3：本机不装 Visual Studio / Windows SDK，WinUI 3 需要 MSIX 打包与 Windows App SDK，而 WPF 用现有 `Microsoft.WindowsDesktop.App.Ref` 即可纯 `dotnet build` 编译，内存占用也更低。
- 输入：Win32 Raw Input（通过 P/Invoke）。
- 蓝牙：**Winsock `AF_BTH` + `BTHPROTO_RFCOMM`（P/Invoke `ws2_32.dll`）**，自行注册 SDP 记录。放弃 `Windows.Devices.Bluetooth.Rfcomm`：非打包 Win32 应用下 WinRT 蓝牙能力受限。

### 模块

```text
TrackLink.Windows/
  Program.cs                  # 双入口：无参=图形界面；selftest/serve/remote=控制台模式（AttachConsole）
  App.cs                      # WPF Application：单实例 Mutex、异常兜底、退出清理钩子
  MainWindow.xaml(.cs)        # 主窗口：左侧导航 + 右侧三视图切换、托盘、全局停止热键
  Settings/
    AppSettings.cs            # %LOCALAPPDATA%\TrackLink\settings.json 读写
    GestureOptions.cs         # 灵敏度 / 边缘加速 / 反向滚动 / 停止热键（手势线程现读，改动立即生效）
  Input/
    RawInputListener.cs       # 注册和接收 WM_INPUT（含设备活动时间与选定分组键过滤；消息窗口类名须每实例唯一）
    TouchpadCatalog.cs        # 列举输入设备（取代计划中的 DeviceCatalog / TouchpadFilter 两项职责）
    HidTouchpadDecoder.cs     # 多触点 HID 报文解码（HidP_GetData + 索引映射）
    GestureRecognizer.cs      # 输入事件转控制命令（含光标移动、拖拽状态机、三指手势）
    LocalMouseBlocker.cs      # 本机鼠标闸门（WH_MOUSE_LL，计划书未提及的补充机制）
    RemoteCommand.cs          # 命令类型与 payload 编码
  Bluetooth/
    RfcommHost.cs             # 创建和广播 RFCOMM 服务
    BtSession.cs              # 会话收发、心跳 PING/PONG 与 RTT（取代计划中的 ProtocolWriter 编码职责）
    SdpRecordBuilder.cs       # SDP 记录构造（WSASetService + BTH_SET_SERVICE）
    BtSockets.cs              # ws2_32 / SOCKADDR_BTH / BTH_SET_SERVICE 等 P/Invoke 声明
    SessionCrypto.cs          # 会话认证与加密（第 5 周，尚未实现）
  Protocol.cs                 # 二进制协议编解码（帧头 10 字节）
  UI/
    RemoteEngine.cs           # 编排蓝牙 + 输入 + 识别器 + 鼠标闸门，对界面暴露线程安全快照
    UiLogSink.cs              # 日志汇聚（Dispatcher 编组 + 定长 500 行缓冲）
    DevicePickerView.xaml(.cs)
    ConnectionView.xaml(.cs)
    SettingsView.xaml(.cs)
    TrayIcon.cs               # 托盘图标与右键菜单
    ConsoleMode.cs            # selftest / serve / remote 三个控制台模式实现
```

### 触控板设备识别

不要依赖 `WM_MOUSEMOVE`，因为该消息已经被 Windows 归一化，通常不保留设备来源。应使用 `WM_INPUT`：

1. 注册鼠标/HID 类 Raw Input。
2. 从 `RAWINPUTHEADER.hDevice` 获取来源设备句柄。
3. 调用 `GetRawInputDeviceInfo` 缓存设备路径、名称与能力。
4. 首次运行时，让用户选择“内置触控板”；显示厂商名、设备路径和最近活动时间。
5. 要求用户滑动触控板 3 秒，高亮产生事件的设备作为确认。
6. 保存设备路径指纹；下次启动自动匹配。匹配失败时要求重新选择。

若某些旧驱动将触控板和鼠标完全合并，程序必须显示“不支持仅触控板过滤”，不能猜测来源。

**本机实测到的识别规则（可直接采用）**

多触点集合与同一块触控板的鼠标集合共享设备路径第 3 段，仅末位 `&xxxx` 不同：

```
\\?\HID#ETD2303&Col03#5&1d09e9c1&0&0002#{...}   ← 多触点集合（0x0D/0x05）
\\?\HID#ETD2303&Col01#5&1d09e9c1&0&0000#{...}   ← 鼠标集合
分组键 = 第 3 段去掉最后一个 &xxxx = 5&1d09e9c1&0
```

- 判定触控板：凡顶层集合为 `0x0D/0x05` 的 HID 设备，其分组键即"内置触控板"。
- 过滤规则：只转发分组键属于该集合的 MOUSE/HID 事件；外接鼠标分组键必然不同。
- 注意 ELAN 的鼠标集合 `Col01` 在本机从不产生事件（精确式触控板由 PTP 驱动接管），点击需来自多触点集合的 `0x09/0x01` 按钮字段。

### 手势与坐标策略

Android 无障碍服务更适合离散触摸手势，第一版不追求像素级鼠标指针。

- 每 16–25 ms 合并一次 `dx/dy`，减少高频蓝牙报文。
- 移动距离超过阈值后发送 `DRAG`，不要逐条转发原始事件。
- 坐标统一采用 `0–65535` 的归一化范围，Android 端映射到实际屏幕像素。
- 支持灵敏度、边缘加速、反向滚动和安全停止热键。

### 独立软件化：偏离与补充

Windows 端已从"必须开终端敲 `remote`、靠 Ctrl+C 退出"的控制台程序，改造为带图形界面的独立软件。以下为计划书原文未覆盖、或与原文不一致的部分。

**与原文不一致（已按实际落地修订）**

- UI 框架由"WinUI 3 或 WPF"落定为 WPF；蓝牙由 `Windows.Devices.Bluetooth.Rfcomm` 改为 Winsock `AF_BTH`。理由见上文「技术选型」。
- 模块表中 `DeviceCatalog` / `TouchpadFilter` 的实际职责由 `TouchpadCatalog` / `RawInputListener` 承担，`ProtocolWriter` 的编码职责由 `Protocol.cs` + `BtSession` 承担；并新增了 `HidTouchpadDecoder`、`RemoteCommand`、`SdpRecordBuilder`、`BtSockets`。

**补充机制（计划书未提及）**

- **托盘常驻**：窗口关闭 = 最小化到托盘（`Closing` 事件 `e.Cancel = true`），最小化也收进托盘；托盘右键菜单提供「显示主窗口 / 最小化到托盘 / 退出 TrackLink」，双击图标唤起。退出只有一条明确路径（菜单项），避免误关导致手机端"连接中…"悬挂。
- **单实例**：`Local\TrackLink.Windows.SingleInstance` 命名互斥体；重复启动时用 `FindWindow` + `ShowWindow(SW_RESTORE)` + `SetForegroundWindow` 唤起已运行实例。必要性：两个实例各自 `bind(BT_PORT_ANY)` 会拿到不同通道，却注册同一 UUID 的 SDP 记录，手机将发现到两条冲突记录。
- **自包含单文件发布**：`dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false`。`IncludeNativeLibrariesForSelfExtract` 必须加；**禁用 `PublishTrimmed`**（WPF 不支持裁剪），**也不要加 `EnableCompressionInSingleFile`**（见下）。实测产出单个 `publish\win-x64\TrackLink.Windows.exe`（约 138 MB），目标机无需安装 .NET 运行时。
  - **`EnableCompressionInSingleFile=true` 会导致窗口客户区全黑**（本机实测）。去掉压缩后同一份代码渲染完全正常；压缩版虽然能把体积压到约 63 MB，且原生库（`PresentationNative_cor3.dll` / `wpfgfx_cor3.dll` / `D3DCompiler_47_cor3.dll`）也确实被正确提取到 `%TEMP%\.net\TrackLink.Windows\`，但 WPF 渲染管线仍初始化失败，进程不报错、不崩溃、标题栏正常，只有画面全黑。**为省 75 MB 换一个黑屏，不值得，直接放弃压缩。**
- **保留命令行模式**：无参数启动 = 图形界面；`selftest` / `serve` / `remote` 仍走控制台。因 `OutputType=WinExe` 下 `Console.Out` 是 `TextWriter.Null`，需 `AttachConsole(ATTACH_PARENT_PROCESS)`，失败则 `AllocConsole()`，再 `Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true })`（`AutoFlush` 必需）。
- **`LocalMouseBlocker`（本机鼠标闸门）**：remote 模式期间把由触控板产生的本机鼠标输入吞掉，于是手指在触控板上滑动时笔记本光标一动不动、轻点也不会在本机点一下；多触点集合（COL03）的 Raw Input 不受影响，手势照常送达手机。实现为低层鼠标钩子 `WH_MOUSE_LL`，用"触控板上报后 250 ms 时间窗"判定来源，外接鼠标没有前置信号故照常放行，**不需要管理员权限**，进程退出后系统自动摘除钩子。
  - 不用停用设备节点的原因：本机鼠标类节点不止一个（另有 PS/2 的 `ACPI\MSFT0003`、虚拟鼠标 `ROOT\HIDCLASS`），停用 `HID\ETD2303&COL01` 后光标照旧跟着手指动；且停用设备需要管理员权限。
  - 已知残留：Windows 自身的**三指**系统手势（任务视图等）仍会响应，注册表 `ThreeFingerTapEnabled` 等项的开关尝试无效后已撤回。三指手势的手机端命令不受影响。
- **Raw Input 消息窗口的窗口类名必须每实例唯一**（踩过的坑）：窗口类按进程注册，`DestroyWindow` 不会注销它，也不会因窗口销毁而失效。若沿用固定类名，「停止服务」后再「启动服务」时 `RegisterClassEx` 会因类名已存在而失败（Win32 1410），消息窗口建不出来，从此**一条 WM_INPUT 都收不到**——现象是「触控板识别时好时坏」，且重启程序才恢复。修法：类名带上实例 GUID，收尾时 `UnregisterClass`。配套要求：监听启动失败必须显示到界面上（写进 `SelectionProblem` 并让 `SelectTouchpad` 拒绝），不能只写日志——否则「收不到报文」和「收到但没识别」在界面上长得一模一样，无法排查。

**配置与退出**

- 配置文件：`%LOCALAPPDATA%\TrackLink\settings.json`，保存触控板路径指纹（分组键 + 厂商/产品/设备路径）与手势参数；下次启动自动匹配，匹配失败时回到设备选择页要求重选。
- 全局安全停止热键（默认 `Ctrl+Alt+Q`，可改为 `Ctrl+Alt+F12` / `Ctrl+Shift+F12` / 禁用），注册失败会在设置页显式提示，不静默。
- 退出兜底三处均调用幂等的 `LocalMouseBlocker.Stop()`：`Window.Closed`、`DispatcherUnhandledException`、`AppDomain.ProcessExit` + `UnhandledException`。

## Android 客户端

### 技术选型

- Kotlin。
- Jetpack Compose。
- 最低 Android 8（API 26），目标 Android 14+。
- Bluetooth Classic RFCOMM。
- `AccessibilityService`。

### 模块

```text
TrackLink.Android/
  bluetooth/
    BluetoothConnectionManager.kt
    RfcommClient.kt
    ProtocolReader.kt
  accessibility/
    TrackLinkAccessibilityService.kt
    GestureDispatcher.kt
    GlobalActionDispatcher.kt
  core/
    Command.kt
    SessionAuthenticator.kt
    RateLimiter.kt
  ui/
    PairingScreen.kt
    ConnectionScreen.kt
    PermissionGuideScreen.kt
```

### 权限与系统配置

- Android 12+ 申请 `BLUETOOTH_SCAN` 与 `BLUETOOTH_CONNECT`。
- 用户必须在系统设置中手动启用无障碍服务；应用不得自行开启。
- 无障碍服务配置 `android:canPerformGestures="true"`。
- 持续显示通知，提供显眼的停止控制入口。

### 命令执行映射

| 收到的命令 | Android 执行方式 |
|---|---|
| `TAP` | 短时单点 `dispatchGesture()` |
| `DOUBLE_TAP` | 两个连续短点击 |
| `LONG_PRESS` | 长时单点手势 |
| `DRAG_START/MOVE/END` | 按顺序续接的滑动笔画 |
| `SCROLL` | 指定区域的短距离滑动 |
| `BACK/HOME/RECENTS` | `performGlobalAction()` |
| `STOP` | 取消队列中的动作并断开会话 |

`dispatchGesture()` 会取消正在进行的手势，因此所有手势必须由单一串行队列执行，确保拖拽的开始、移动、结束不乱序。

## 通信协议

采用紧凑二进制协议，以降低连续滑动的延迟和 GC 压力。

```text
[magic: 2B][version: 1B][type: 1B][sequence: 4B][length: 2B][payload][MAC: 16B]
```

命令：

```text
HELLO        { protocolVersion, desktopNonce }
AUTH         { phoneNonce, proof }
VIEWPORT     { width, height, density }
TAP          { x16, y16 }
DRAG_START   { pointerId, x16, y16 }
DRAG_MOVE    { pointerId, x16, y16, durationMs }
DRAG_END     { pointerId, x16, y16 }
SCROLL       { deltaX, deltaY }
GLOBAL       { BACK | HOME | RECENTS }
PING/PONG
STOP
```

## 安全设计

- 仅允许用户明确确认过的蓝牙配对设备连接。
- 首次连接显示双方短验证码，确认后保存会话密钥。
- 每帧包含单调递增序号；会话重建时生成新 nonce，以防重放。
- 3 秒心跳超时后立即停止动作并断连。
- 不记录原始触控板轨迹；日志仅保存匿名连接状态和延迟数据。
- 手机端支持一键断开与禁用服务。

## 里程碑

### 第 1 周：输入可行性

- 枚举 Raw Input 设备。
- 验证是否可以稳定识别内置触控板独立设备句柄。
- 控制台打印触控板专属 `dx/dy/button` 事件。

**实测结论（本机 TIMI TM1707 / Windows 10 22H2 19045）——已达成**

验证程序：[TrackLink.Windows.Spike](file:///d:/Code/Projects/TrackLink/TrackLink.Windows.Spike)（.NET 8，控制台模式与 `--window` 可视化模式）。

| 目标 | 结果 | 证据 |
| --- | --- | --- |
| 枚举 Raw Input 设备 | 通过 | 18 个设备；内置触控板为 ELAN `ETD2303&Col03`，VID:PID `04F3:3083`，顶层集合 `0x0D/0x05` |
| 稳定识别触控板独立句柄 | 通过 | 触控板分组键 `5&1d09e9c1&0`；4 个外接鼠标分组键分别为 `7&306cfee4&0`、`1&2d595ca7&0`、`4&1d0fd433`，互不相同 → 可按分组键可靠过滤 |
| 打印触控板专属事件 | 通过 | 单次 75 秒采集到 1346 条触控板报文，X/Y 常年落在逻辑区间 `0..3864` / `0..2499` |
| 多触点能力 | 通过 | 观测到最大接触点 3（双指/三指时按帧交错上报） |

**两条影响后续实现的硬结论**

1. **`HidP_GetUsageValue` 在本机 ELAN 触控板上对全部用途均失败**（NTSTATUS `0xC0110001`）。必须改用 `HidP_GetData`，再按 `HIDP_VALUE_CAPS` / `HIDP_BUTTON_CAPS` 的 `DataIndex` 反查用途。这是本项目输入层的唯一可行解析路径。
2. **`ContactCount` 只在每帧第一条报文里出现**，其余接触点报文的 CC 读数为 0。因此不能把每条 HID 报文当成独立事件，必须按「CC>0 开启一帧 → 依 `ContactID` 累积各接触点 → 直到 `TipSwitch` 全部释放」聚合后再手势判定。

**解析到的触控板能力（rid=4，输入报文 12 字节）**

```
值能力  : 0x0D/0x51 ContactID(4bit, idx2) 0x01/0x30 X(16bit, idx3)
          0x01/0x31 Y(16bit, idx4)        0x0D/0x56 ScanTime(16bit, idx5)
          0x0D/0x54 ContactCount(8bit, idx6) 0x09/0xC5 x2(8bit, idx8..9)
按钮能力: 0x0D/0x42 TipSwitch(1bit, idx0) 0x0D/0x47 Confidence(1bit, idx1)
          0x09/0x01 物理按键(1bit, idx7)
```

**物理按键已验证**：真实按下触控板（有"咔哒"回馈）时，`0x09/0x01` 置位，报文首部出现 `BTN=1`（如 `04 03 F9 01 8B 08 68 17 01 81 1D 44`，byte9 由常态 `80` 变为 `81`）。实测一轮采集到 30 条。硬件点击与软件"轻点"是两条独立通道，两者都要接。

### 第 2 周：蓝牙链路

- Windows RFCOMM 服务端。
- Android 扫描、连接、`PING/PONG`。
- 已配对设备自动重连和超时断线。

**Windows 端进展（已完成）**

工程 `TrackLink.Windows`（`net8.0-windows`，纯 P/Invoke，无 WinRT）：`bind → getsockname → listen → 注册 SDP → accept`，加上帧协议、`PING/PONG` 心跳与 3 秒超时断线。

自检结果（`TrackLink.Windows.exe selftest`）：

```
① 服务端绑定与监听       通过（本机地址 FC:77:74:BE:1A:BC，RFCOMM 通道 4）
② SDP 记录可被按 UUID 查询 通过（回查得到地址/通道 4/服务名，与注册一致）
③ 回环建立连接并收到 PONG  未通过（connect 自身地址返回 10051 WSAENETUNREACH）
```

第 ③ 项是预期结果：Windows 蓝牙栈不支持连接自身地址做回环。该项已改用 Android 真机补验通过（见下文「真机联调结果」）。

第 2 周里程碑中的「已配对设备自动重连」尚未实现，本期只做到手动断开后可立刻重新连接（服务端 accept 循环支持连续会话）；自动重连放到第 5 周与稳定性一起做。

**两个耗时较久的坑（已定位，均与「结构体布局/传参格式」有关）**

1. `bind` 恒返回 `WSAEADDRNOTAVAIL(10049)`。
   根因：原生 `SOCKADDR_BTH` 被 `pshpack1.h` 包裹，是 30 字节 1 字节对齐；C# 默认 8 字节对齐会得到 40 字节，使 `btAddr`/`port` 偏移整体错位，协议栈实际读到 port=0。
   修复：`[StructLayout(LayoutKind.Sequential, Pack = 1)]`。注意 40 字节"看起来像正常大小"反而是误导信号。
   （顺带确认：`BTHPROTO_RFCOMM` 下 `BT_PORT_ANY` 会分配通道 4，指定通道需显式 bind。）

2. `WSASetService` 注册 SDP 时抛 `AccessViolationException`（进程退出码 3221225477）。
   根因：`lpBlob` 指向的 `BLOB.pBlobData` 必须是 **`BTH_SET_SERVICE`** 结构，而不是裸 SDP 记录字节。我们把 SDP 记录直接当成了 `BTH_SET_SERVICE`，于是记录开头的 4 字节被当成 `ulRecordLength`，系统据此越界读取 `pRecord`。
   迷惑点：`BTH_SET_SERVICE.pRecord` 的偏移正好是 **44**，所以「记录 ≤ 44 字节」时会读到 `pRecord` 为空/长度恰为 0 而"注册成功"，45 字节起必崩——这个 44 字节阈值是巧合，不是真实限制。
   修复：按 `BTH_SET_SERVICE` 铺设（`pSdpVersion` 指向 `BTH_SDP_VERSION`=1、`pRecordHandle` 指向初值 0 的 HANDLE、`ulRecordLength`=记录长度、记录紧跟在偏移 44 处），`BLOB.cbSize = 44 + 记录长度`。修复后完整 109 字节记录（含服务名、BrowseGroupList、LanguageBase、ProfileDescriptorList）注册正常。
   另一处：删除记录必须用 `RNRSERVICE_DELETE`，Bluetooth 命名空间下 `RNRSERVICE_DEREGISTER` 无效（返回 `WSAEINVAL(10022)`）。

**Android 端进展（已完成）**

工程 `TrackLink.Android`（Kotlin + Jetpack Compose，AGP 8.5.2 / Gradle 8.7 / JDK 17，`compileSdk 34`、`minSdk 26`、`targetSdk 34`，包名 `com.tracklink.remote`）。

- `bluetooth/Protocol.kt`：与 Windows 端逐字节一致的帧编解码 + 增量 `FrameParser`（magic 前垃圾、非法版本、超长 payload 均逐字节丢弃）。
- `bluetooth/BluetoothConnectionManager.kt`：已配对设备列表 / 扫描 / `createRfcommSocketToServiceRecord` 连接 / HELLO 握手 / 1 秒 PING 心跳 / 3 秒超时断线，状态与日志以 `StateFlow` 暴露给界面。
- `ui/ConnectionScreen.kt` + `MainActivity.kt`：权限申请（API 31+ 用 `BLUETOOTH_SCAN`/`BLUETOOTH_CONNECT`，API 30 及以下用 `ACCESS_FINE_LOCATION`）、设备列表、连接状态、实时 RTT、日志。

**真机联调结果（Redmi K20 Pro / Android 11 / MIUI）——第 ② 项自检的补验**

设备是 Android 11，不是计划里的 Android 13+；协议的权限分支正好覆盖到这一档，验证有效。

| 验证项 | 结果 | 证据 |
| --- | --- | --- |
| 手机与笔记本配对 | 通过 | 手机侧 `FC:77:74:BE:1A:BC [BR/EDR] DESKTOP-VUKP1P3` |
| 按 UUID 发现并建连 | 通过 | Android 日志「连接建立，耗时 878 ms」；Windows 侧 accept 成功 |
| 双向握手 | 通过 | 两端均打印「对端协议版本 1，一致」（Android 主动发 HELLO，Windows 建连后也发 HELLO） |
| PING/PONG 与 RTT | 通过 | Android 界面实时显示 RTT；单次会话 719 个样本：中位 31 ms、均值 28.4 ms、最大 141 ms |
| 连续 12 分钟不掉线 | 通过 | 同一会话 719 次心跳，断开/超时/错误事件计数 = 0（**已满足 MVP「连续 10 分钟不掉线」验收项**） |
| 其他已配对设备不干扰 | 通过 | 用 `createRfcommSocketToServiceRecord(本项目 UUID)` 精确匹配，列表里的蓝牙游戏散热背夹从未被连接 |

测试期间把手机设为屏幕常亮（`adb shell svc power stayon true`），避免息屏/Doze 冻结进程造成与链路无关的假掉线。

**第三个耗时较久的坑：Android 会话被放在主线程上**

现象：连接成功、界面却永远停在「连接中…」，RTT 一直显示空。Windows 侧却能持续收到 PONG —— 因为主线程正阻塞在 `input.read()` 里替对端回 PONG，而它自己的心跳协程（`lifecycleScope.launch` 默认 Main）根本没机会运行，UI 也无法重绘。

修复：`sessionJob = scope.launch(Dispatchers.IO) { … }`，会话内的心跳改用 `coroutineScope { launch { heartbeatLoop() } }` 继承同一个 IO 上下文。结论：**RFCOMM 的 connect/read/write 全是阻塞调用，整条会话必须整体离开主线程**，只把单次 `connect` 丢到 IO 线程是不够的。

顺带记两条 MIUI 实操约束：安装 debug 包必须在开发者选项里打开「USB 安装」与「USB 调试（安全设置）」，否则报 `INSTALL_FAILED_USER_RESTRICTED`；`adb shell input tap` 被 MIUI 拦截（`INJECT_EVENTS` 缺失），界面点击只能人工操作，截图可以正常用 `adb shell screencap` 取证。

### 第 3 周：最小控制闭环

- 实现 `TAP`、`SCROLL`、`BACK`。
- 无障碍服务配置页与权限引导。
- 用网页浏览、列表滚动、返回主页验证。

### 第 4 周：手势与调校

- 实现拖拽、双击、长按。
- 实现坐标映射、灵敏度、平滑和加速。
- 处理断线、旋转屏幕和应用切换。

### 第 5 周：安全与稳定性

- 会话认证、重放保护和心跳。
- 压测、功耗、后台重连。
- 至少覆盖 3 个 Android 品牌或系统版本。

### 第 6 周：发布准备

- 隐私说明与无障碍功能用途说明。
- 首次使用向导和诊断报告导出。
- 形成兼容设备清单与不支持情形说明。

## 测试清单

- 外接 USB/蓝牙鼠标与触控板并存时，仅转发触控板。
- 手机横竖屏切换后，坐标映射正确。
- 蓝牙关闭、睡眠、来电、锁屏、离开范围时安全停止。
- 高频滚动、连续拖拽、双指滚动与手掌误触。
- 无障碍服务被关闭后，拒绝执行操作并提示用户。
- 畸形、重放、乱序报文必须被丢弃。
- 支付、锁屏与系统安全界面被系统拦截时，应用安全失败。

## 风险与应对

| 风险 | 应对 |
|---|---|
| 触控板与鼠标输入无法区分 | 第 1 周先做兼容性检测；不支持时明确提示。 |
| Android 对无障碍服务限制 | 只用公开 API 的点击、滑动、全局操作；不承诺系统鼠标指针。 |
| 蓝牙断连或延迟波动 | 心跳、自动重连、断线即停、事件批处理。 |
| 应用商店审核 | 明确辅助/远程操控用途；不做隐蔽控制、广告点击或规避限制。 |
| 交互不像传统鼠标 | 产品叙事与交互定位为“远程触控板手势”。 |

## 参考资料

- [Microsoft Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)
- [Windows RFCOMM service](https://learn.microsoft.com/en-us/uwp/api/windows.devices.bluetooth.rfcomm.rfcommserviceprovider?view=winrt-28000)
- [Android Bluetooth 连接指南](https://developer.android.com/develop/connectivity/bluetooth/connect-bluetooth-devices)
- [Android AccessibilityService](https://developer.android.com/reference/android/accessibilityservice/AccessibilityService)
- [创建 Android 无障碍服务](https://developer.android.com/guide/topics/ui/accessibility/service)
