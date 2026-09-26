using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TrackLink.Windows.Spike;

/// <summary>
/// 第 1 周可行性探测：枚举 Raw Input 设备、验证触控板拥有独立的可识别句柄，
/// 并实时打印触控板专属事件（含多触点集合的报文解析）。
/// </summary>
internal static class Program
{
    private sealed class DeviceEntry
    {
        public IntPtr Handle;
        public string Path = "";
        public string ShortName = "";
        public string GroupKey = "";
        public uint Type;
        public uint VendorId;
        public uint ProductId;
        public ushort UsagePage;
        public ushort Usage;
        public bool IsTouchpadCollection;
        public int EventCount;
        public int PrintedCount;
        public int SuppressedCount;
        public string LastSignature = "";
        public DateTime WindowStart = DateTime.UtcNow;
        public int WindowCount;
        public TouchpadDecoder? Decoder;
    }

    private static readonly List<DeviceEntry> DeviceOrder = new();
    private static readonly Dictionary<IntPtr, DeviceEntry> Devices = new();
    private static readonly HashSet<string> TouchpadGroupKeys = new();

    private static IntPtr _window;
    private static WndProcDelegate? _wndProc;
    private static bool _stop;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _seconds;
    private static bool _onlyTouchpad;
    private static bool _showHex;
    private static bool _noThrottle;
    private static bool _useWindow;
    private static MonitorForm? _form;
    private static string? _logPath;
    private const int LinesPerSecondPerDevice = 12;

    /// <summary>把输出同时写到控制台与日志文件，避免管道编码影响中文。</summary>
    private sealed class TeeWriter(TextWriter primary, TextWriter secondary) : TextWriter
    {
        public override Encoding Encoding => primary.Encoding;

        public override void Write(char value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void Write(string? value)
        {
            primary.Write(value);
            secondary.Write(value);
        }

        public override void WriteLine(string? value)
        {
            primary.WriteLine(value);
            secondary.WriteLine(value);
        }

        public override void Flush()
        {
            primary.Flush();
            secondary.Flush();
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (!ParseArgs(args))
        {
            return 1;
        }

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 输出被重定向时可能不支持设置编码，忽略。
        }

        return _useWindow ? RunWindowMode() : RunConsoleMode();
    }

    private static int RunConsoleMode()
    {
        StreamWriter? log = null;
        if (_logPath is not null)
        {
            log = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };
            Console.SetOut(new TeeWriter(Console.Out, log));
        }

        Console.WriteLine("TrackLink · 第 1 周可行性探测（Raw Input 触控板独立性验证）");
        Console.WriteLine(new string('-', 78));

        if (!CreateMessageWindow())
        {
            Console.WriteLine("创建消息窗口失败，无法继续。");
            return 1;
        }

        RegisterRawInput();
        EnumerateDevices();

        Console.WriteLine(new string('-', 78));
        Console.WriteLine("开始采集。请依次操作：");
        Console.WriteLine("  1) 单指在触控板上滑动约 3 秒");
        Console.WriteLine("  2) 双指在触控板上上下滑动");
        Console.WriteLine("  3) 轻点一下触控板（单击）");
        Console.WriteLine("  4) 移动 / 点击外接鼠标（用于验证能否被排除）");
        Console.WriteLine(_seconds > 0
            ? $"将在 {_seconds:0} 秒后自动结束。"
            : "按 Ctrl+C 结束采集。");
        Console.WriteLine(new string('-', 78));

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _stop = true;
        };

        MessageLoop();
        PrintSummary();

        foreach (var device in DeviceOrder)
        {
            device.Decoder?.Dispose();
        }

        if (_window != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_window);
        }

        Console.Out.Flush();
        log?.Dispose();

        return 0;
    }

    /// <summary>
    /// 可见窗口模式：所有采集输出实时显示在窗口里，由用户自己控制节奏（想点几下就点几下）。
    /// </summary>
    private static int RunWindowMode()
    {
        var original = Console.Out;
        StreamWriter? log = null;
        if (_logPath is not null)
        {
            log = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        }

        Application.EnableVisualStyles();
        _form = new MonitorForm();
        _window = _form.Handle;

        Console.SetOut(log is null
            ? new FormWriter(_form)
            : new TeeWriter(new FormWriter(_form), log));

        Console.WriteLine("TrackLink · 第 1 周可行性探测（触控板独立句柄 / 多触点 / 按键）");
        Console.WriteLine(new string('-', 78));

        RegisterRawInput();
        EnumerateDevices();

        Console.WriteLine(new string('-', 78));
        Console.WriteLine("请在窗口中实时观察，并依次操作触控板：");
        Console.WriteLine("  1) 单指滑动      → X/Y 连续变化，CC=1 CID=0 TIP=1");
        Console.WriteLine("  2) 双指上下滑    → 每帧 CC=2 CID=0 与 CC=0 CID=1 交替");
        Console.WriteLine("  3) 轻点 / 按下   → 出现 ★ 按键提示（BTN=1）");
        Console.WriteLine("  4) 再动外接鼠标  → 应当被窗口过滤掉（分组键不同）");
        Console.WriteLine("观察完成后直接关闭本窗口即可。");
        Console.WriteLine(new string('-', 78));

        Application.Run(_form);

        Console.SetOut(log is null ? original : new TeeWriter(original, log));
        PrintSummary();

        foreach (var device in DeviceOrder)
        {
            device.Decoder?.Dispose();
        }

        Console.Out.Flush();
        log?.Dispose();
        _form = null;

        return 0;
    }

    /// <summary>窗口内的日志框：由 Console 输出重定向驱动，避免改动既有打印代码。</summary>
    private sealed class FormWriter : TextWriter
    {
        private readonly MonitorForm _target;

        public FormWriter(MonitorForm target) => _target = target;

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value) => _target.Append(value ?? "");

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var text = value.Replace("\r\n", "\n").TrimEnd('\n');
            foreach (var line in text.Split('\n'))
            {
                _target.Append(line);
            }
        }
    }

    private sealed class MonitorForm : Form
    {
        private readonly TextBox _output;
        private readonly Label _status;
        private readonly Button _clear;
        private readonly Button _toggle;

        public MonitorForm()
        {
            Text = "TrackLink · 触控板验证窗口";
            Width = 960;
            Height = 620;
            StartPosition = FormStartPosition.CenterScreen;

            _output = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = ScrollBars.Both,
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(24, 24, 24),
                ForeColor = Color.Gainsboro,
            };

            _status = new Label
            {
                Dock = DockStyle.Top,
                Height = 30,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(8, 0, 0, 0),
                Text = "正在监听…",
            };

            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(8, 8, 0, 0),
            };

            _clear = new Button { Text = "清空", Width = 90, Height = 26 };
            _clear.Click += (_, _) => _output.Clear();

            _toggle = new Button { Text = "仅显示触控板：开", Width = 170, Height = 26 };
            _toggle.Click += (_, _) =>
            {
                _onlyTouchpad = !_onlyTouchpad;
                _toggle.Text = _onlyTouchpad ? "仅显示触控板：开" : "仅显示触控板：关";
            };

            bar.Controls.Add(_clear);
            bar.Controls.Add(_toggle);

            Controls.Add(_output);
            Controls.Add(_status);
            Controls.Add(bar);
        }

        public void Append(string line)
        {
            if (line.Contains("BTN=1", StringComparison.Ordinal))
            {
                line = "★ 触控板按键按下（BTN=1） → " + line;
            }

            _output.AppendText(line + Environment.NewLine);
            if (_output.TextLength > 300_000)
            {
                _output.Text = _output.Text[^150_000..];
                _output.SelectionStart = _output.TextLength;
            }

            RefreshStatus();
        }

        private void RefreshStatus()
        {
            var pads = DeviceOrder.Where(d => d.IsTouchpadCollection).ToList();
            int reports = pads.Sum(d => d.EventCount);
            int maxContacts = pads.Count == 0 ? 0 : pads.Max(d => d.Decoder?.MaxContactsObserved ?? 0);
            _status.Text = $"触控板报文 {reports}   最大接触点 {maxContacts}   "
                           + $"| 轻点触控板应出现 ★ 按键提示；外接鼠标不应出现在此窗口";
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32Constants.WM_INPUT)
            {
                HandleRawInput(m.LParam);
            }
            else if (m.Msg == Win32Constants.WM_INPUT_DEVICE_CHANGE)
            {
                HandleDeviceChange(m.WParam, m.LParam);
            }

            base.WndProc(ref m);
        }
    }

    private static bool ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds":
                    if (i + 1 < args.Length && double.TryParse(args[++i], out double seconds))
                    {
                        _seconds = seconds;
                    }
                    break;
                case "--only-touchpad":
                    _onlyTouchpad = true;
                    break;
                case "--hex":
                    _showHex = true;
                    break;
                case "--no-throttle":
                    _noThrottle = true;
                    break;
                case "--window":
                    _useWindow = true;
                    break;
                case "--log":
                    if (i + 1 < args.Length)
                    {
                        _logPath = args[++i];
                    }
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine("用法: RawInputProbe [--window] [--seconds N] [--only-touchpad] [--hex] [--no-throttle] [--log 文件]");
                    Console.WriteLine("  --window         打开可见窗口，实时显示事件，由用户自己控制操作节奏");
                    Console.WriteLine("  --seconds N      采集 N 秒后自动结束（默认 Ctrl+C 结束）");
                    Console.WriteLine("  --only-touchpad  仅打印来自触控板硬件的事件（验证过滤能力）");
                    Console.WriteLine("  --hex            打印完整 HID 报文十六进制");
                    Console.WriteLine("  --no-throttle    不限制打印速率（输出量很大）");
                    Console.WriteLine("  --log 文件       同时把输出写入 UTF-8 日志文件");
                    return false;
                default:
                    Console.WriteLine($"未知参数: {args[i]}");
                    return false;
            }
        }

        return true;
    }

    // ---------- 窗口与 Raw Input 注册 ----------

    private static bool CreateMessageWindow()
    {
        _wndProc = WndProc;
        const string className = "TrackLinkRawInputProbeWindow";

        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = className,
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
        {
            Console.WriteLine($"RegisterClassEx 失败（Win32 {Marshal.GetLastWin32Error()}）");
            return false;
        }

        _window = NativeMethods.CreateWindowEx(
            0, className, null, 0, 0, 0, 0, 0,
            Win32Constants.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);

        if (_window == IntPtr.Zero)
        {
            Console.WriteLine($"CreateWindowEx 失败（Win32 {Marshal.GetLastWin32Error()}）");
            return false;
        }

        return true;
    }

    private static void RegisterRawInput()
    {
        RegisterOne(Win32Constants.UsagePageGenericDesktop, Win32Constants.UsageGenericMouse);
        RegisterOne(Win32Constants.UsagePageDigitizer, Win32Constants.UsageDigitizerTouchPad);
    }

    private static void RegisterOne(ushort usagePage, ushort usage)
    {
        var devices = new[]
        {
            new RAWINPUTDEVICE
            {
                usUsagePage = usagePage,
                usUsage = usage,
                dwFlags = Win32Constants.RIDEV_INPUTSINK | Win32Constants.RIDEV_DEVNOTIFY,
                hwndTarget = _window,
            },
        };

        bool ok = NativeMethods.RegisterRawInputDevices(
            devices, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

        string label = $"usagePage=0x{usagePage:X2} usage=0x{usage:X2}"
                       + $"（{TouchpadDecoder.DescribeTopLevel(usagePage, usage)}）";
        Console.WriteLine(ok
            ? $"[注册成功] {label}"
            : $"[注册失败] {label} → Win32 {Marshal.GetLastWin32Error()}");
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32Constants.WM_INPUT:
                HandleRawInput(lParam);
                break;
            case Win32Constants.WM_INPUT_DEVICE_CHANGE:
                HandleDeviceChange(wParam, lParam);
                break;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void MessageLoop()
    {
        while (!_stop)
        {
            while (NativeMethods.PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, Win32Constants.PM_REMOVE))
            {
                if (msg.message == 0x0012)
                {
                    _stop = true;
                    break;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }

            if (_seconds > 0 && Clock.Elapsed.TotalSeconds >= _seconds)
            {
                break;
            }

            Thread.Sleep(1);
        }
    }

    // ---------- 设备枚举 ----------

    private static void EnumerateDevices()
    {
        uint count = 0;
        uint entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (NativeMethods.GetRawInputDeviceList(null, ref count, entrySize) == Win32Constants.RawInputDataError || count == 0)
        {
            Console.WriteLine("GetRawInputDeviceList 失败，未发现任何 Raw Input 设备。");
            return;
        }

        var list = new RAWINPUTDEVICELIST[count];
        uint actual = NativeMethods.GetRawInputDeviceList(list, ref count, entrySize);
        if (actual == Win32Constants.RawInputDataError)
        {
            Console.WriteLine("GetRawInputDeviceList（读取）失败。");
            return;
        }

        var entries = new List<DeviceEntry>();
        for (uint i = 0; i < actual; i++)
        {
            var entry = BuildEntry(list[i].hDevice, list[i].dwType);
            entries.Add(entry);
            Devices[entry.Handle] = entry;
            DeviceOrder.Add(entry);
        }

        // 先判定触控板分组键：凡暴露 0x0D/0x05 顶层集合的 HID 设备即触控板硬件。
        foreach (var entry in entries.Where(e => e.IsTouchpadCollection))
        {
            TouchpadGroupKeys.Add(entry.GroupKey);
        }

        Console.WriteLine($"共发现 {entries.Count} 个 Raw Input 设备：");
        Console.WriteLine();
        Console.WriteLine($"{"类型",-7} {"设备",-30} {"VID:PID",-11} {"顶层集合",-14} {"分组键",-14} 说明");
        Console.WriteLine(new string('-', 110));

        foreach (var entry in entries)
        {
            string usageText = entry.Type == Win32Constants.RIM_TYPEHID
                ? $"0x{entry.UsagePage:X2}/0x{entry.Usage:X2}"
                : "-";

            string note = "";
            if (entry.IsTouchpadCollection)
            {
                note = "★ 触控板多触点集合（0x0D/0x05 自动识别）";
            }
            else if (entry.Type == Win32Constants.RIM_TYPEMOUSE && TouchpadGroupKeys.Contains(entry.GroupKey))
            {
                note = "触控板硬件的鼠标集合（同分组键）";
            }
            else if (entry.Type == Win32Constants.RIM_TYPEMOUSE)
            {
                note = "外部/其他鼠标（分组键不同，可被过滤）";
            }

            Console.WriteLine($"{TypeText(entry.Type),-7} {entry.ShortName,-30} {entry.VendorId:X4}:{entry.ProductId:X4}   "
                              + $"{usageText,-14} {entry.GroupKey,-14} {note}");
        }

        Console.WriteLine();
        if (TouchpadGroupKeys.Count == 0)
        {
            Console.WriteLine("⚠ 未发现任何 0x0D/0x05 触控板多触点集合 —— 本机无法按多触点集合识别触控板。");
            return;
        }

        foreach (var entry in entries.Where(e => e.IsTouchpadCollection))
        {
            Console.WriteLine($"触控板多触点集合详情: {entry.Path}");
            var decoder = new TouchpadDecoder(entry.Path);
            entry.Decoder = decoder;
            var sb = new StringBuilder();
            decoder.Describe(sb);
            Console.Write(sb.ToString());
            Console.WriteLine();
        }
    }

    private static DeviceEntry BuildEntry(IntPtr hDevice, uint type)
    {
        string path = GetDeviceName(hDevice) ?? "(未知路径)";
        var (vendorId, productId, usagePage, usage) = GetDeviceInfo(hDevice);

        bool isTouchpad = type == Win32Constants.RIM_TYPEHID
                          && usagePage == Win32Constants.UsagePageDigitizer
                          && usage == Win32Constants.UsageDigitizerTouchPad;

        return new DeviceEntry
        {
            Handle = hDevice,
            Path = path,
            ShortName = ShortName(path),
            GroupKey = GroupKey(path),
            Type = type,
            VendorId = vendorId,
            ProductId = productId,
            UsagePage = usagePage,
            Usage = usage,
            IsTouchpadCollection = isTouchpad,
        };
    }

    private static string TypeText(uint type) => type switch
    {
        Win32Constants.RIM_TYPEMOUSE => "MOUSE",
        Win32Constants.RIM_TYPEKEYBOARD => "KEYBD",
        Win32Constants.RIM_TYPEHID => "HID",
        _ => $"T{type}",
    };

    /// <summary>设备路径形如 \\?\HID#ETD2303&amp;COL03#5&amp;1D09E9C1&amp;0&amp;0002#{GUID}。</summary>
    private static string ShortName(string path)
    {
        var segments = path.Split('#');
        return segments.Length >= 2 ? segments[1] : path;
    }

    /// <summary>去掉实例序列号，得到“物理设备”级别的分组键，用于把 COL01/COL03 归为同一块触控板。</summary>
    private static string GroupKey(string path)
    {
        var segments = path.Split('#');
        if (segments.Length < 3)
        {
            return path;
        }

        string instance = segments[2];
        int cut = instance.LastIndexOf('&');
        return cut > 0 ? instance[..cut] : instance;
    }

    private static string? GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        NativeMethods.GetRawInputDeviceInfo(hDevice, Win32Constants.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, Win32Constants.RIDI_DEVICENAME, buffer, ref size);
            return result == Win32Constants.RawInputDataError ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static (uint VendorId, uint ProductId, ushort UsagePage, ushort Usage) GetDeviceInfo(IntPtr hDevice)
    {
        const int infoSize = 32;
        IntPtr buffer = Marshal.AllocHGlobal(infoSize);
        try
        {
            for (int i = 0; i < infoSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt32(buffer, 0, infoSize);
            uint size = infoSize;
            uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, Win32Constants.RIDI_DEVICEINFO, buffer, ref size);
            if (result == Win32Constants.RawInputDataError)
            {
                return (0, 0, 0, 0);
            }

            var bytes = new byte[infoSize];
            Marshal.Copy(buffer, bytes, 0, infoSize);

            // RID_DEVICE_INFO: cbSize(4) dwType(4) union(24)；HID 分支为 VID/PID/Version/UsagePage/Usage。
            return (BitConverter.ToUInt32(bytes, 8), BitConverter.ToUInt32(bytes, 12),
                    BitConverter.ToUInt16(bytes, 20), BitConverter.ToUInt16(bytes, 22));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void HandleDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        bool arrived = wParam == Win32Constants.GIDC_ARRIVAL;
        string? path = GetDeviceName(lParam);
        Console.WriteLine($"[设备{(arrived ? "接入" : "移除")}] {path ?? "(未知)"}");
    }

    // ---------- 事件处理 ----------

    private static void HandleRawInput(IntPtr lParam)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (NativeMethods.GetRawInputData(lParam, Win32Constants.RID_INPUT, IntPtr.Zero, ref size, headerSize)
            == Win32Constants.RawInputDataError || size == 0)
        {
            return;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            uint read = NativeMethods.GetRawInputData(lParam, Win32Constants.RID_INPUT, buffer, ref size, headerSize);
            if (read == Win32Constants.RawInputDataError || read == 0)
            {
                return;
            }

            var bytes = new byte[read];
            Marshal.Copy(buffer, bytes, 0, (int)read);
            ProcessRawInput(bytes, (int)headerSize);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ProcessRawInput(byte[] bytes, int headerSize)
    {
        uint type = BitConverter.ToUInt32(bytes, 0);
        IntPtr hDevice = IntPtr.Size == 8
            ? (IntPtr)BitConverter.ToInt64(bytes, 8)
            : (IntPtr)BitConverter.ToInt32(bytes, 8);

        if (!Devices.TryGetValue(hDevice, out var device))
        {
            device = new DeviceEntry { Handle = hDevice, ShortName = "(未知设备)" };
            Devices[hDevice] = device;
            DeviceOrder.Add(device);
        }

        device.EventCount++;

        if (type == Win32Constants.RIM_TYPEMOUSE)
        {
            ProcessMouse(device, bytes, headerSize);
        }
        else if (type == Win32Constants.RIM_TYPEHID)
        {
            ProcessHid(device, bytes, headerSize);
        }
    }

    private static void ProcessMouse(DeviceEntry device, byte[] bytes, int headerSize)
    {
        ushort buttonFlags = BitConverter.ToUInt16(bytes, headerSize + 2);
        ushort buttonData = BitConverter.ToUInt16(bytes, headerSize + 4);
        int dx = BitConverter.ToInt32(bytes, headerSize + 10);
        int dy = BitConverter.ToInt32(bytes, headerSize + 14);

        string detail = $"dx={dx,5} dy={dy,5}";
        if (buttonFlags != 0)
        {
            detail += $" 按钮={DescribeMouseButtons(buttonFlags)}";
        }
        if ((buttonFlags & Win32Constants.RI_MOUSE_WHEEL) != 0)
        {
            detail += $" 滚轮={unchecked((short)buttonData)}";
        }
        if ((buttonFlags & Win32Constants.RI_MOUSE_HWHEEL) != 0)
        {
            detail += $" 横向滚轮={unchecked((short)buttonData)}";
        }

        // 按键变化一定打印；纯移动按速率限制。
        Print(device, "[MOUSE]", detail, force: buttonFlags != 0, signature: $"b{buttonFlags:X4}");
    }

    private static void ProcessHid(DeviceEntry device, byte[] bytes, int headerSize)
    {
        uint sizeHid = BitConverter.ToUInt32(bytes, headerSize + 0);
        uint reportCount = BitConverter.ToUInt32(bytes, headerSize + 4);
        if (sizeHid == 0 || reportCount == 0 || sizeHid > 4096)
        {
            return;
        }

        int dataStart = headerSize + 8;
        for (uint i = 0; i < reportCount; i++)
        {
            int offset = dataStart + (int)(i * sizeHid);
            if (offset + (int)sizeHid > bytes.Length)
            {
                break;
            }

            var report = new byte[sizeHid];
            Array.Copy(bytes, offset, report, 0, (int)sizeHid);

            if (device.Decoder is not null)
            {
                var (line, signature) = device.Decoder.Decode(report, _showHex);
                Print(device, "[HID 触控板]", line, force: signature != device.LastSignature, signature: signature);
            }
            else
            {
                Print(device, "[HID]", $"rid={report[0]} len={report.Length} hex={TouchpadDecoder.ToHex(report, 24)}",
                      force: false, signature: TouchpadDecoder.ToHex(report, 12));
            }
        }
    }

    private static string DescribeMouseButtons(ushort flags)
    {
        var parts = new List<string>();
        if ((flags & Win32Constants.RI_MOUSE_LEFT_BUTTON_DOWN) != 0) parts.Add("左键↓");
        if ((flags & Win32Constants.RI_MOUSE_LEFT_BUTTON_UP) != 0) parts.Add("左键↑");
        if ((flags & Win32Constants.RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) parts.Add("右键↓");
        if ((flags & Win32Constants.RI_MOUSE_RIGHT_BUTTON_UP) != 0) parts.Add("右键↑");
        if ((flags & Win32Constants.RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) parts.Add("中键↓");
        if ((flags & Win32Constants.RI_MOUSE_MIDDLE_BUTTON_UP) != 0) parts.Add("中键↑");
        return string.Join("+", parts);
    }

    // ---------- 输出 ----------

    private static void Print(DeviceEntry device, string tag, string detail, bool force, string signature)
    {
        bool fromTouchpad = device.IsTouchpadCollection
                            || (device.Type == Win32Constants.RIM_TYPEMOUSE && TouchpadGroupKeys.Contains(device.GroupKey));

        if (_onlyTouchpad && !fromTouchpad)
        {
            device.SuppressedCount++;
            return;
        }

        if (!_noThrottle)
        {
            var now = DateTime.UtcNow;
            if ((now - device.WindowStart).TotalSeconds >= 1)
            {
                if (device.SuppressedCount > 0)
                {
                    Console.WriteLine($"{device.ShortName,-30} （上一秒省略 {device.SuppressedCount} 条）");
                    device.SuppressedCount = 0;
                }

                device.WindowStart = now;
                device.WindowCount = 0;
            }

            bool stateChanged = signature != device.LastSignature;
            if (!force && !stateChanged && device.WindowCount >= LinesPerSecondPerDevice)
            {
                device.SuppressedCount++;
                return;
            }

            device.WindowCount++;
        }

        device.LastSignature = signature;
        device.PrintedCount++;
        Console.WriteLine($"{tag,-12} {device.ShortName,-30} {detail}");
    }

    private static void PrintSummary()
    {
        Console.WriteLine();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine("采集汇总");
        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"{"设备",-30} {"类型",-7} {"分组键",-14} {"事件数",8}  说明");
        Console.WriteLine(new string('-', 100));

        foreach (var device in DeviceOrder)
        {
            bool fromTouchpad = device.IsTouchpadCollection
                                || (device.Type == Win32Constants.RIM_TYPEMOUSE && TouchpadGroupKeys.Contains(device.GroupKey));

            string note = device.IsTouchpadCollection
                ? $"★ 触控板多触点集合，最大接触点数={device.Decoder?.MaxContactsObserved ?? 0}"
                : fromTouchpad
                    ? "触控板硬件派生"
                    : device.Type == Win32Constants.RIM_TYPEMOUSE ? "外部鼠标" : "";

            Console.WriteLine($"{device.ShortName,-30} {TypeText(device.Type),-7} {device.GroupKey,-14} "
                              + $"{device.EventCount,8}  {note}");
        }

        Console.WriteLine();
        Console.WriteLine(new string('-', 78));
        Console.WriteLine("第 1 周判定素材");
        Console.WriteLine(new string('-', 78));

        var touchpadCollections = DeviceOrder.Where(d => d.IsTouchpadCollection).ToList();
        if (touchpadCollections.Count == 0)
        {
            Console.WriteLine("✗ 未识别到 0x0D/0x05 触控板多触点集合。");
            return;
        }

        foreach (var device in touchpadCollections)
        {
            Console.WriteLine($"✓ 触控板多触点集合 : {device.Path}");
            Console.WriteLine($"  分组键={device.GroupKey}  HID 报文事件={device.EventCount}  "
                              + $"观察到的最大接触点数={device.Decoder?.MaxContactsObserved ?? 0}");
        }

        Console.WriteLine($"✓ 触控板物理设备分组键 : {string.Join(", ", TouchpadGroupKeys)}");

        var externalMice = DeviceOrder
            .Where(d => d.Type == Win32Constants.RIM_TYPEMOUSE && !TouchpadGroupKeys.Contains(d.GroupKey))
            .ToList();
        int externalEvents = externalMice.Sum(d => d.EventCount);
        Console.WriteLine($"  外接鼠标设备数={externalMice.Count}，事件数={externalEvents}"
                          + "（分组键与触控板不同 → 可被可靠排除）");

        int touchpadReports = touchpadCollections.Sum(d => d.EventCount);
        Console.WriteLine(touchpadReports > 0
            ? "✓ 结论：触控板具备独立的可识别句柄，且能读到多触点报文 → 第 1 周目标达成。"
            : "✗ 结论：识别到集合但本次未收到报文，请重新滑动触控板后再判定。");
    }
}