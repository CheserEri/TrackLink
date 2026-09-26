using System.Runtime.InteropServices;

namespace TrackLink.Windows.Input;

/// <summary>
/// 本机鼠标闸门：remote 模式期间把**由触控板产生**的本机鼠标输入吞掉，
/// 于是手指在触控板上滑动时笔记本光标一动不动、轻点也不会在本机点一下，
/// 而多触点集合（COL03）的 Raw Input 完全不受影响，手势照常送达手机。
///
/// <para>
/// 为什么不用停用设备节点：实测本机的鼠标类节点不止一个——除了 <c>HID\ETD2303&amp;COL01</c>，
/// 还有 PS/2 的 <c>ACPI\MSFT0003</c>（服务 <c>i8042prt</c>）与虚拟鼠标 <c>ROOT\HIDCLASS</c>（服务 <c>mouhid</c>）。
/// 把 COL01 停用后光标照旧跟着手指动，说明真正驱动光标的是别的路径；而且停用设备需要管理员权限。
/// 低层鼠标钩子（<c>WH_MOUSE_LL</c>）与来源无关，一次全拦，**不需要管理员权限**，
/// 进程退出后钩子由系统自动摘除，不会给机器留下任何残留状态。
/// </para>
///
/// <para>
/// 判定「是否触控板输入」用时间相关性：触控板每上报一份报文就记一次时间戳，
/// 系统触控板栈合成的鼠标消息紧随其后到达，落在 <see cref="ActiveWindowMs"/> 窗口内即判为触控板输入；
/// 外接鼠标没有这个前置信号，照常放行。
/// </para>
/// </summary>
internal static class LocalMouseBlocker
{
    /// <summary><c>WH_MOUSE_LL</c>。</summary>
    private const int WhMouseLl = 14;

    private const uint WmQuit = 0x0012;

    /// <summary>触控板上报之后，继续吞掉本机鼠标输入的时间窗（毫秒）。</summary>
    private const long ActiveWindowMs = 250;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>必须用字段持有，否则委托被回收后钩子会静默失效。</summary>
    private static readonly LowLevelMouseProc Proc = OnMouse;

    private static IntPtr _hook;
    private static Thread? _thread;
    private static uint _threadId;

    /// <summary>最近一次触控板上报的时刻；初始 0 表示还没上报过。</summary>
    private static long _lastReportMs;

    /// <summary>未被吞掉时最后一次鼠标位置，钩子兜底时把光标挪回这里。</summary>
    private static Point _anchor;

    /// <summary>本机鼠标闸门是否在工作。</summary>
    public static bool Active => _hook != IntPtr.Zero;

    /// <summary>收到一份触控板报文时调用。</summary>
    public static void NoteTouchpadReport() =>
        Interlocked.Exchange(ref _lastReportMs, Environment.TickCount64);

    /// <summary>在后台线程装钩子并跑消息循环。</summary>
    public static bool Start(out string? error)
    {
        if (Active)
        {
            error = null;
            return true;
        }

        string? failure = null;
        using var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(WhMouseLl, Proc, IntPtr.Zero, 0);
            failure = _hook == IntPtr.Zero
                ? $"安装低层鼠标钩子失败：Win32 错误 {Marshal.GetLastWin32Error()}"
                : null;
            ready.Set();

            if (_hook == IntPtr.Zero)
            {
                return;
            }

            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }

            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        })
        {
            IsBackground = true,
            Name = "TrackLink 本机鼠标闸门",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();

        error = failure;
        return failure is null;
    }

    /// <summary>摘掉钩子；进程意外结束时系统也会自动摘除，所以这里只是干净收尾。</summary>
    public static void Stop()
    {
        var thread = _thread;
        if (thread is null)
        {
            return;
        }

        PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        thread.Join(2000);
        _thread = null;
    }

    private static IntPtr OnMouse(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<MsllHookStruct>(lParam);
        if (Environment.TickCount64 - Interlocked.Read(ref _lastReportMs) > ActiveWindowMs)
        {
            _anchor = info.Pt;
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // 吞掉这条输入。钩子理论上跑在光标更新之前，但版本行为并不一致，
        // 所以再把光标挪回锚点，确保「本机光标一动不动」。
        SetCursorPos(_anchor.X, _anchor.Y);
        return new IntPtr(1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    /// <summary><c>MSLLHOOKSTRUCT</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MsllHookStruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary><c>MSG</c>。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetMessage(out Msg message, IntPtr hWnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}