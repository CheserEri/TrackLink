using System.Runtime.InteropServices;

namespace TrackLink.Windows.Input;

/// <summary>
/// 触摸板 Raw Input 监听器。
///
/// 只注册触控板多触点集合 <c>0x0D/0x05</c>：外接鼠标属于 <c>0x01/0x02</c>，天然不会进入本监听器，
/// 因此「外接鼠标不影响手机」这一项由注册范围本身保证；随后仍按分组键做一次兜底过滤。
/// 消息窗口放在独立线程上泵，避免阻塞调用方的线程。
///
/// <para>
/// 设备选择（计划书「触控板设备识别」）：<see cref="SelectedGroupKey"/> 为空时**只做活动检测**，
/// 一条手势都不转发——这样设备选择页可以在用户还没选定时就展示「哪块设备正在产生事件」。
/// 选定后才把该分组键的报文交给上层做手势识别。
/// </para>
/// </summary>
internal sealed class RawInputListener : IDisposable
{
    private const string WindowClassName = "TrackLinkRawInputSink";

    /// <summary>
    /// 本次实例使用的窗口类名。**必须每个实例唯一**：窗口类按进程注册，<c>DestroyWindow</c> 不会注销它，
    /// 若沿用固定名字，「停止服务」后再「启动服务」会因类名已存在而注册失败，
    /// 监听器从此不再收到任何 WM_INPUT——表现为「触控板识别时好时坏」。
    /// </summary>
    private readonly string _windowClassName = $"{WindowClassName}-{Guid.NewGuid():N}";

    /// <summary>候选设备与活动时刻会被界面线程读取，用锁保证安全。</summary>
    private readonly object _deviceLock = new();

    private readonly Dictionary<IntPtr, TouchpadDevice> _devices = [];
    private readonly HashSet<string> _touchpadKeys = [];
    private readonly ManualResetEventSlim _ready = new(false);

    /// <summary>各设备最近一次上报时刻（TickCount64），从未上报过则无记录。</summary>
    private readonly Dictionary<IntPtr, long> _lastActivity = [];

    /// <summary>当前活动统计窗口内各设备的报文数。</summary>
    private readonly Dictionary<IntPtr, int> _windowCounts = [];

    private string? _selectedGroupKey;
    private volatile bool _windowActive;

    private WndProcDelegate? _wndProc;
    private IntPtr _window = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _windowOk;

    /// <summary>累计收到的 WM_INPUT 条数（含尚未选定的设备）。用于诊断「触控板识别不稳定」。</summary>
    private long _inputMessages;

    public event Action<string>? Log;

    /// <summary>每条触控板 HID 报文的解码结果。仅在选定触控板后触发。</summary>
    public event Action<TouchContactReport>? Report;

    /// <summary>
    /// 当前选定的触控板分组键。为空表示尚未选定：只记录设备活动，不转发手势。
    /// </summary>
    public string? SelectedGroupKey
    {
        get => Volatile.Read(ref _selectedGroupKey);
        set => Volatile.Write(ref _selectedGroupKey, value);
    }

    /// <summary>枚举到的触控板候选设备快照，供设备选择页展示。</summary>
    public TouchpadDevice[] Candidates
    {
        get
        {
            lock (_deviceLock)
            {
                return [.. _devices.Values];
            }
        }
    }

    /// <summary>按分组键取候选设备（含其解析器）。</summary>
    public TouchpadDevice? FindByGroupKey(string groupKey)
    {
        lock (_deviceLock)
        {
            foreach (var device in _devices.Values)
            {
                if (device.GroupKey == groupKey)
                {
                    return device;
                }
            }
        }

        return null;
    }

    /// <summary>设备最近一次上报距今多少毫秒；从未上报过返回 null。</summary>
    public long? ActivityAgeMs(IntPtr handle)
    {
        lock (_deviceLock)
        {
            return _lastActivity.TryGetValue(handle, out var ticks)
                ? Environment.TickCount64 - ticks
                : null;
        }
    }

    /// <summary>开始一次活动统计窗口（设备选择页的「滑动 3 秒」）。</summary>
    public void BeginActivityWindow()
    {
        lock (_deviceLock)
        {
            _windowCounts.Clear();
        }

        _windowActive = true;
    }

    /// <summary>结束活动统计窗口。</summary>
    public void EndActivityWindow() => _windowActive = false;

    /// <summary>本活动窗口内该设备收到多少条报文。</summary>
    public int WindowReportCount(IntPtr handle)
    {
        lock (_deviceLock)
        {
            return _windowCounts.GetValueOrDefault(handle);
        }
    }

    /// <summary>主触控板（第一个 0x0D/0x05 集合）的解析器，用于取归一化范围与首条报文诊断。</summary>
    public TouchpadDecoder? PrimaryDecoder { get; private set; }

    public string? TouchpadGroupKey { get; private set; }

    /// <summary>消息窗口与 Raw Input 注册是否都成功。为 false 时本监听器永远收不到输入。</summary>
    public bool Started => _windowOk;

    /// <summary>累计收到的 WM_INPUT 条数。</summary>
    public long InputMessageCount => Interlocked.Read(ref _inputMessages);

    public bool Start()
    {
        DiscoverDevices();

        if (_touchpadKeys.Count == 0)
        {
            Log?.Invoke("[错误] 未发现 0x0D/0x05 触控板多触点集合，触控板输入不可用。");
            return false;
        }

        _thread = new Thread(PumpThread) { IsBackground = true, Name = "rawinput-pump" };
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            Log?.Invoke("[错误] 等待 Raw Input 消息窗口就绪超时。");
            return false;
        }

        return _windowOk;
    }

    public void Stop()
    {
        _stop = true;

        if (_window != IntPtr.Zero)
        {
            // 让阻塞在 PeekMessage 的泵线程立刻醒来，从而退出消息循环。
            NativeMethods.PostMessage(_window, Win32Constants.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose()
    {
        Stop();

        foreach (var device in _devices.Values)
        {
            device.Decoder?.Dispose();
            device.Decoder = null;
        }

        _devices.Clear();
        _ready.Dispose();
    }

    /// <summary>枚举设备，并为每个触控板多触点集合建立解析器。</summary>
    private void DiscoverDevices()
    {
        var entries = TouchpadCatalog.Enumerate();
        Log?.Invoke($"[输入] 共发现 {entries.Count} 个 Raw Input 设备，其中触控板多触点集合：");

        foreach (var entry in entries)
        {
            if (!entry.IsTouchpadCollection)
            {
                continue;
            }

            lock (_deviceLock)
            {
                _devices[entry.Handle] = entry;
                _touchpadKeys.Add(entry.GroupKey);
            }

            entry.Decoder = new TouchpadDecoder(entry.Path);
            if (!entry.Decoder.Ready)
            {
                Log?.Invoke($"[输入] ✗ {entry.ShortName} 解析器不可用：{entry.Decoder.Error}");
                continue;
            }

            var source = entry.Decoder.UsingFallbackRanges ? "第 1 周实测回退值" : "能力表 LogicalMin/Max";
            Log?.Invoke($"[输入] ✓ {entry.ShortName}（VID:PID {entry.VendorId:X4}:{entry.ProductId:X4}，"
                        + $"分组键 {entry.GroupKey}）"
                        + $"X {entry.Decoder.XRange.Min}..{entry.Decoder.XRange.Max} "
                        + $"Y {entry.Decoder.YRange.Min}..{entry.Decoder.YRange.Max}，来源 {source}");

            PrimaryDecoder ??= entry.Decoder;
            TouchpadGroupKey ??= entry.GroupKey;
        }
    }

    private void PumpThread()
    {
        if (!CreateMessageWindow())
        {
            _ready.Set();
            return;
        }

        if (!RegisterRawInput())
        {
            CleanupWindow();
            _ready.Set();
            return;
        }

        _windowOk = true;
        _ready.Set();

        while (!_stop)
        {
            while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, Win32Constants.PM_REMOVE))
            {
                if (msg.message == Win32Constants.WM_QUIT)
                {
                    _stop = true;
                    break;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            if (_stop)
            {
                break;
            }

            Thread.Sleep(1);
        }

        CleanupWindow();
    }

    /// <summary>销毁消息窗口并注销窗口类，避免同一进程内反复启停时累积注册。</summary>
    private void CleanupWindow()
    {
        if (_window != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }

        NativeMethods.UnregisterClass(_windowClassName, NativeMethods.GetModuleHandle(null));
    }

    private bool CreateMessageWindow()
    {
        _wndProc = WndProc;

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = _windowClassName,
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            Log?.Invoke($"[错误] RegisterClassEx 失败，Win32 {Marshal.GetLastWin32Error()}");
            return false;
        }

        _window = NativeMethods.CreateWindowEx(
            0, _windowClassName, null, 0, 0, 0, 0, 0,
            Win32Constants.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            Log?.Invoke($"[错误] CreateWindowEx 失败，Win32 {Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    private bool RegisterRawInput()
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = Win32Constants.UsagePageDigitizer,
                usUsage = Win32Constants.UsageDigitizerTouchPad,
                dwFlags = Win32Constants.RIDEV_INPUTSINK,
                hwndTarget = _window,
            },
        };

        if (!NativeMethods.RegisterRawInputDevices(devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            Log?.Invoke($"[错误] 注册触控板 Raw Input 失败，Win32 {Marshal.GetLastWin32Error()}");
            return false;
        }

        Log?.Invoke("[输入] 已注册触控板多触点集合 0x0D/0x05（RIDEV_INPUTSINK），实时监听中…");
        return true;
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32Constants.WM_INPUT)
        {
            HandleRawInput(lParam);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr lParam)
    {
        Interlocked.Increment(ref _inputMessages);

        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (NativeMethods.GetRawInputData(lParam, Win32Constants.RID_INPUT, IntPtr.Zero, ref size, headerSize)
            == Win32Constants.RawInputDataError || size == 0)
        {
            return;
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            var read = NativeMethods.GetRawInputData(lParam, Win32Constants.RID_INPUT, buffer, ref size, headerSize);
            if (read == Win32Constants.RawInputDataError || read == 0)
            {
                return;
            }

            var bytes = new byte[read];
            Marshal.Copy(buffer, bytes, 0, (int)read);
            Process(bytes, (int)headerSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Process(byte[] bytes, int headerSize)
    {
        if (BitConverter.ToUInt32(bytes, 0) != Win32Constants.RIM_TYPEHID)
        {
            return;
        }

        var hDevice = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(bytes, 8)
            : (IntPtr)BitConverter.ToInt32(bytes, 8);

        var device = ResolveDevice(hDevice);
        if (device?.Decoder is null || !_touchpadKeys.Contains(device.GroupKey))
        {
            return;
        }

        // 无论是否已选定，都记录活动：设备选择页要靠它判断「哪块设备正在产生事件」。
        RecordActivity(device);

        // 尚未选定时只做活动检测，不往上层转发，避免误把别的触控板当输入源。
        if (device.GroupKey != SelectedGroupKey)
        {
            return;
        }

        var sizeHid = BitConverter.ToUInt32(bytes, headerSize);
        var reportCount = BitConverter.ToUInt32(bytes, headerSize + 4);
        if (sizeHid == 0 || reportCount == 0 || sizeHid > 4096)
        {
            return;
        }

        var dataStart = headerSize + 8;
        for (uint i = 0; i < reportCount; i++)
        {
            var offset = dataStart + (int)(i * sizeHid);
            if (offset + (int)sizeHid > bytes.Length)
            {
                break;
            }

            var report = new byte[sizeHid];
            Array.Copy(bytes, offset, report, 0, (int)sizeHid);

            if (!device.Decoder.TryDecode(report, out var decoded))
            {
                continue;
            }

            // 首条报文的能力表/字段明细只打印一次，用于核对解码布局。
            if (device.FirstDump is null && device.Decoder.FirstReportDump is { } dump)
            {
                device.FirstDump = dump;
                Log?.Invoke($"[输入] {device.ShortName} 首条报文与能力表：\n{dump}");
            }

            Report?.Invoke(decoded);
        }
    }

    /// <summary>启动后才接入的触控板设备按需补建解析器；不属于触控板的句柄返回 null。</summary>
    private TouchpadDevice? ResolveDevice(IntPtr hDevice)
    {
        lock (_deviceLock)
        {
            if (_devices.TryGetValue(hDevice, out var cached))
            {
                return cached;
            }
        }

        var entry = TouchpadCatalog.BuildEntry(hDevice, Win32Constants.RIM_TYPEHID);
        if (!entry.IsTouchpadCollection)
        {
            return null;
        }

        lock (_deviceLock)
        {
            _devices[hDevice] = entry;
            _touchpadKeys.Add(entry.GroupKey);
        }

        entry.Decoder = new TouchpadDecoder(entry.Path);

        if (entry.Decoder.Ready)
        {
            Log?.Invoke($"[输入] 检测到触控板 {entry.ShortName}（分组键 {entry.GroupKey}），已开始监听。");
            PrimaryDecoder ??= entry.Decoder;
            TouchpadGroupKey ??= entry.GroupKey;
        }

        return entry;
    }

    /// <summary>记录一次上报，供设备选择页判断哪块设备在产生事件。</summary>
    private void RecordActivity(TouchpadDevice device)
    {
        lock (_deviceLock)
        {
            _lastActivity[device.Handle] = Environment.TickCount64;

            if (_windowActive)
            {
                _windowCounts[device.Handle] = _windowCounts.GetValueOrDefault(device.Handle) + 1;
            }
        }
    }
}