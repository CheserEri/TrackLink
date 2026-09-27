using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TrackLink.Windows.Settings;
using TrackLink.Windows.UI;

namespace TrackLink.Windows;

/// <summary>
/// 主窗口：左侧导航 + 三个视图（触控板设备 / 连接与控制 / 手势设置）、托盘常驻、
/// 全局停止热键、250 ms 状态轮询。
///
/// 关窗口 = 最小化到托盘（服务与手势转发继续在后台生效），只有托盘「退出」才真正结束。
/// </summary>
public partial class MainWindow : System.Windows.Window
{
    private const int WmHotkey = 0x0312;
    private const long HotkeyId = 0x54;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly UiLogSink _sink;
    private readonly RemoteEngine _engine;
    private readonly DispatcherTimer _poll;

    private TrayIcon? _tray;
    private HwndSource? _source;
    private bool _hookAdded;
    private bool _hotkeyRegistered;
    private bool _exiting;
    private ViewKind _view = ViewKind.Device;
    private StickerKey _sideStickerKey = StickerKey.Connecting;

    private enum ViewKind
    {
        Device,
        Connection,
        Settings,
    }

    public MainWindow()
    {
        InitializeComponent();

        _sink = new UiLogSink(Dispatcher);
        _engine = new RemoteEngine(_settings);
        _engine.Log += _sink.Post;

        // 窗口 / 任务栏图标用品牌贴纸；解码失败时 BrandIcon 内部会回退到系统图标。
        Icon = BrandIcon.Source;

        _poll = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _poll.Tick += OnPollTick;

        Closed += OnWindowClosed;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口整体淡入。只挂动画、不改 Opacity 基值：动画万一没生效，窗口仍是默认不透明。
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));

        DevicePicker.Initialize(_engine, _settings, _sink, OnTouchpadSelected);
        Connection.Initialize(_engine, _sink);
        GestureSettings.Initialize(_settings, _sink);
        GestureSettings.StopHotkeyChanged += ApplyHotkey;

        _tray = new TrayIcon(ShowFromTray, HideToTray, ExitApplication);
        _sink.Post($"[启动] 图形界面已就绪。配置文件：{AppSettings.Location}");

        ApplyHotkey();
        _poll.Start();

        // 启动欢迎：派蒙开心（动图），随后由链路阶段接管。
        SetSideSticker(StickerKey.Welcome, "欢迎回来");

        if (!_engine.Start())
        {
            _sink.Post("[服务] 首次启动失败：请确认蓝牙已开启，然后在「连接与控制」页重试。");
            DevicePicker.SetHint("服务未启动，触控板监听不可用。请确认蓝牙已开启后到「连接与控制」页重试。");
            Navigate(ViewKind.Device);
            return;
        }

        if (_engine.SelectedGroupKey is not null)
        {
            _sink.Post("[启动] 已按上次保存的指纹自动匹配到触控板，直接进入连接与控制。");
            Navigate(ViewKind.Connection);
            return;
        }

        DevicePicker.SetHint(_engine.SelectionProblem ?? "请在触控板上滑动 3 秒完成检测，然后点「就用它」。");
        Navigate(ViewKind.Device);
    }

    /// <summary>关窗口 → 隐藏到托盘；只有托盘「退出」才真正结束进程。</summary>
    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _poll.Stop();
        _sink.Post("[退出] 正在释放触控板监听、蓝牙服务与本机鼠标闸门…");

        GestureSettings.StopHotkeyChanged -= ApplyHotkey;
        ReleaseHotkey();
        _source?.RemoveHook(WndProcHook);

        // 清理顺序：先停监听与解绑手势事件，再关蓝牙，最后无论如何都要摘掉鼠标钩子（在 Dispose 的 finally 里）。
        _engine.Dispose();
        _tray?.Dispose();
        _tray = null;
    }

    private void OnPollTick(object? sender, EventArgs e)
    {
        Connection.Refresh();

        if (_view == ViewKind.Device)
        {
            DevicePicker.Refresh();
        }

        RefreshSideStatus();
    }

    /// <summary>
    /// 侧栏状态：贴纸由链路阶段决定，文案由服务状态补齐。
    /// 贴纸只在语义真正变化时才换图，否则每 250 ms 重设一次 Key 会让入场动画反复重播。
    /// </summary>
    private void RefreshSideStatus()
    {
        if (!_engine.IsRunning)
        {
            // 服务没起来：界面必须把「没启动」和「启动了但没连上」分开，否则用户完全无从下手。
            // 贴纸下方已经写明「服务未启动」，状态行不再复述，只留触控板信息（原来两处重复）。
            SetSideSticker(StickerKey.Bye, "服务未启动");
            SideStatusText.Text = _engine.TouchpadText;
            return;
        }

        switch (_engine.Phase)
        {
            case LinkPhase.Connected:
                SetSideSticker(_engine.IsPaused ? StickerKey.Paused : StickerKey.Connected,
                    _engine.IsPaused ? "已暂停（喝口茶）" : "已连接");
                break;

            case LinkPhase.Timeout:
                SetSideSticker(StickerKey.Timeout, "心跳超时");
                break;

            case LinkPhase.Lost:
                SetSideSticker(StickerKey.Lost, "连接断开了");
                break;

            default:
                SetSideSticker(StickerKey.Waiting, "等待手机连接");
                break;
        }

        // 运行时必须点明「服务运行中」并给每行加标签：原来只堆三行裸值
        // （等待手机连接 / — / —），用户看不出服务到底起没起来、这些「—」又是什么。
        var rttText = _engine.RttMs is { } rtt ? $"{rtt} ms" : "—";
        SideStatusText.Text = $"服务运行中 · {_engine.ConnectionText}\n"
                              + $"远端 {_engine.RemoteText} · 往返 {rttText}";
    }

    private void SetSideSticker(StickerKey key, string caption)
    {
        SideStickerCaption.Text = caption;

        if (_sideStickerKey == key)
        {
            return;
        }

        _sideStickerKey = key;
        SideSticker.Key = key;
    }

    private void OnTouchpadSelected()
    {
        Navigate(ViewKind.Connection);
        Connection.Refresh();
    }

    private void OnNavigateDevice(object sender, RoutedEventArgs e) => Navigate(ViewKind.Device);

    private void OnNavigateConnection(object sender, RoutedEventArgs e) => Navigate(ViewKind.Connection);

    private void OnNavigateSettings(object sender, RoutedEventArgs e) => Navigate(ViewKind.Settings);

    private void Navigate(ViewKind view)
    {
        _view = view;

        var target = view switch
        {
            ViewKind.Device => (FrameworkElement)DevicePicker,
            ViewKind.Connection => Connection,
            _ => GestureSettings,
        };

        foreach (var page in new FrameworkElement[] { DevicePicker, Connection, GestureSettings })
        {
            page.Visibility = ReferenceEquals(page, target) ? Visibility.Visible : Visibility.Collapsed;
        }

        NavDeviceButton.FontWeight = view == ViewKind.Device ? FontWeights.Bold : FontWeights.Normal;
        NavConnectionButton.FontWeight = view == ViewKind.Connection ? FontWeights.Bold : FontWeights.Normal;
        NavSettingsButton.FontWeight = view == ViewKind.Settings ? FontWeights.Bold : FontWeights.Normal;

        PlayEntrance(target);

        if (view == ViewKind.Device)
        {
            DevicePicker.Refresh();
        }
    }

    /// <summary>
    /// 切页入场：淡入 + 从下方 12 px 上浮。
    /// 每次导航都新建 <see cref="TranslateTransform"/> 实例——Style 里共享同一个 Freezable 时，
    /// 动画会作用到所有引用它的元素上。
    /// </summary>
    private static void PlayEntrance(FrameworkElement page)
    {
        var slide = new TranslateTransform(0, 12);
        page.RenderTransform = slide;

        page.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
        slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private void ShowFromTray()
    {
        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    private void HideToTray()
    {
        Hide();
        _sink.Post("[界面] 已最小化到托盘：手势转发仍在后台生效，右键托盘图标可退出。");
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    /// <summary>热键状态既显示在设置页，也显示在侧栏——随时都能看到急停键是否真的生效。</summary>
    private void SetHotkeyStatus(string text)
    {
        GestureSettings.SetHotkeyStatus(text);
        SideHotkeyText.Text = text;
    }

    /// <summary>按设置重新注册停止热键；注册失败就在设置页显式提示，不静默失败。</summary>
    private void ApplyHotkey()
    {
        ReleaseHotkey();

        var handle = EnsureHook();
        if (handle == IntPtr.Zero)
        {
            SetHotkeyStatus("✗ 窗口句柄不可用，热键未生效。");
            return;
        }

        var (modifiers, virtualKey, name) = ParseHotkey(_settings.Gesture.StopHotkey);
        if (virtualKey == 0)
        {
            SetHotkeyStatus("已禁用停止热键。");
            return;
        }

        if (RegisterHotKey(handle, HotkeyId, modifiers, virtualKey))
        {
            _hotkeyRegistered = true;
            SetHotkeyStatus($"已生效：按 {name} 暂停/恢复控制。");
            return;
        }

        var error = Marshal.GetLastWin32Error();
        SetHotkeyStatus($"✗ 热键注册失败（可能被别的程序占用）：Win32 {error}");
        _sink.Post($"[热键] ✗ {name} 注册失败，Win32 {error}。可在设置页换一个热键。");
    }

    private void ReleaseHotkey()
    {
        if (!_hotkeyRegistered || _source is null)
        {
            return;
        }

        UnregisterHotKey(_source.Handle, HotkeyId);
        _hotkeyRegistered = false;
    }

    /// <summary>拿到窗口句柄并挂上消息钩子（只挂一次），供 WM_HOTKEY 使用。</summary>
    private IntPtr EnsureHook()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (!_hookAdded)
        {
            _source = HwndSource.FromHwnd(handle);
            _source?.AddHook(WndProcHook);
            _hookAdded = true;
        }

        return handle;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey || wParam.ToInt64() != HotkeyId)
        {
            return IntPtr.Zero;
        }

        _engine.IsPaused = !_engine.IsPaused;
        _sink.Post(_engine.IsPaused ? "[热键] 已暂停手势转发。" : "[热键] 已恢复手势转发。");
        Connection.Refresh();
        handled = true;
        return IntPtr.Zero;
    }

    /// <summary>把设置里的热键名称翻译成 RegisterHotKey 需要的修饰键与虚拟键码；「禁用」返回 0。</summary>
    private static (uint Modifiers, uint VirtualKey, string Name) ParseHotkey(string name) => name switch
    {
        "Ctrl+Alt+Q" => (ModControl | ModAlt, 0x51, name),
        "Ctrl+Alt+F12" => (ModControl | ModAlt, 0x7B, name),
        "Ctrl+Shift+F12" => (ModControl | ModShift, 0x7B, name),
        _ => (0, 0, name),
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr window, long id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr window, long id);
}