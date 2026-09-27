using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Animation;
// WinForms 目标框架会隐式引入 System.Drawing，那里也有 Brush / Brushes，必须显式指定 WPF 的。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace TrackLink.Windows.UI;

/// <summary>
/// 连接与控制页（计划书第 47 行的手动「连接、断开、暂停控制」）。
///
/// 状态全部来自 <see cref="RemoteEngine"/> 的线程安全快照，由 MainWindow 的 250 ms 定时器驱动刷新，
/// 因此本页不需要自己处理线程编组，只处理按钮点击。
/// </summary>
internal partial class ConnectionView : System.Windows.Controls.UserControl
{
    private RemoteEngine? _engine;
    private UiLogSink? _sink;

    private Brush _trackBrush = Brushes.Gray;
    private Brush _okBrush = Brushes.Green;
    private Brush _warnBrush = Brushes.Orange;
    private Brush _badBrush = Brushes.Red;
    private Brush _subBrush = Brushes.Gray;

    private StickerKey _stickerKey = StickerKey.Waiting;
    private bool _pulsing;

    public ConnectionView()
    {
        InitializeComponent();
    }

    public void Initialize(RemoteEngine engine, UiLogSink sink)
    {
        _engine = engine;
        _sink = sink;

        // 画笔在这里取一次就够：主题字典在 App 启动时已合并，之后不会再换，不必每 250 ms 查一次资源。
        _trackBrush = (Brush)FindResource("TrackBrush");
        _okBrush = (Brush)FindResource("OkBrush");
        _warnBrush = (Brush)FindResource("WarnBrush");
        _badBrush = (Brush)FindResource("BadBrush");
        _subBrush = (Brush)FindResource("TextSubBrush");

        LogList.ItemsSource = sink.Lines;
        sink.Lines.CollectionChanged += OnLogLinesChanged;

        PauseCheck.IsChecked = engine.IsPaused;
        BlockCheck.IsChecked = engine.MouseBlockEnabled;
        Refresh();
    }

    /// <summary>按引擎快照刷新界面（由 MainWindow 的定时器调用）。</summary>
    public void Refresh()
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        ServiceStatusText.Text = engine.ServiceText;
        ConnectionStatusText.Text = engine.ConnectionText;
        RemoteText.Text = engine.RemoteText;
        RttText.Text = engine.RttMs is { } rtt ? $"{rtt} ms（最近一次心跳往返）" : "—";
        GestureText.Text = engine.LastGestureText;
        TouchpadText.Text = engine.TouchpadText;

        StartButton.IsEnabled = !engine.IsRunning;
        StopButton.IsEnabled = engine.IsRunning;
        DisconnectButton.IsEnabled = engine.ConnectionText == "已连接";

        UpdateLinkVisuals(engine);

        // 暂停态可能被全局热键改掉，这里同步勾选框（赋值前先摘掉事件，避免回写触发保存）。
        if (PauseCheck.IsChecked != engine.IsPaused)
        {
            PauseCheck.IsChecked = engine.IsPaused;
        }
    }

    /// <summary>
    /// 把链路阶段翻译成表情、状态色与心跳信号条。
    ///
    /// 心跳超时必须是独立一格视觉：它和对端正常断开在文案上原来都叫「等待手机连接」，
    /// 用户根本看不出「手机主动挂了」和「手机没回应」的区别。
    /// </summary>
    private void UpdateLinkVisuals(RemoteEngine engine)
    {
        var (key, caption, color) = engine.Phase switch
        {
            LinkPhase.Connected when engine.IsPaused => (StickerKey.Paused, "已暂停（喝口茶）", _warnBrush),
            LinkPhase.Connected => (StickerKey.Connected, "已连接", _okBrush),
            LinkPhase.Timeout => (StickerKey.Timeout, "心跳超时", _badBrush),
            LinkPhase.Lost => (StickerKey.Lost, "连接断开了", _badBrush),
            LinkPhase.Stopped => (StickerKey.Bye, "服务已停止", _subBrush),
            _ => (StickerKey.Waiting, "等待手机连接", _subBrush),
        };

        ConnectionStatusText.Foreground = color;
        LinkStickerCaption.Text = caption;

        if (_stickerKey != key)
        {
            _stickerKey = key;
            LinkSticker.Key = key;
        }

        SetPulse(engine.Phase == LinkPhase.Connected && !engine.IsPaused);
        UpdateHeartbeat(engine);

        // 手势识别是否真的在出结果：连上了但一条手势都没有时，别硬夸它正常。
        var hasGesture = engine.Phase == LinkPhase.Connected && engine.LastGestureText != "（暂无）";
        GestureStickerPanel.Visibility = hasGesture ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>连接中让状态文字缓慢呼吸；断开后必须停掉，否则残留动画会一直占着合成线程。</summary>
    private void SetPulse(bool on)
    {
        if (on == _pulsing)
        {
            return;
        }

        _pulsing = on;

        if (!on)
        {
            ConnectionStatusText.BeginAnimation(OpacityProperty, null);
            ConnectionStatusText.Opacity = 1;
            return;
        }

        ConnectionStatusText.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>四格信号条：按最近一次 RTT 点亮，未连接或还没收到 PONG 时全部熄灭。</summary>
    private void UpdateHeartbeat(RemoteEngine engine)
    {
        var rtt = engine.RttMs;
        var level = engine.Phase != LinkPhase.Connected || rtt is null
            ? 0
            : rtt <= 25 ? 4
            : rtt <= 60 ? 3
            : rtt <= 150 ? 2
            : 1;

        var lit = level >= 3 ? _okBrush : _warnBrush;
        var bars = new[] { HeartbeatBar1, HeartbeatBar2, HeartbeatBar3, HeartbeatBar4 };

        for (var i = 0; i < bars.Length; i++)
        {
            bars[i].Fill = i < level ? lit : _trackBrush;
        }
    }

    private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (AutoScrollCheck.IsChecked != true || _sink is null || _sink.Lines.Count == 0)
        {
            return;
        }

        LogList.ScrollIntoView(_sink.Lines[^1]);
    }

    private void OnStartService(object sender, RoutedEventArgs e) => _engine?.Start();

    private void OnStopService(object sender, RoutedEventArgs e) => _engine?.Stop();

    private void OnDisconnectSession(object sender, RoutedEventArgs e) => _engine?.DisconnectSession();

    private void OnPauseChanged(object sender, RoutedEventArgs e)
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        engine.IsPaused = PauseCheck.IsChecked == true;
        _sink?.Post(engine.IsPaused ? "[控制] 已暂停手势转发。" : "[控制] 已恢复手势转发。");
    }

    private void OnBlockChanged(object sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            _engine.MouseBlockEnabled = BlockCheck.IsChecked == true;
        }
    }

    private void OnClearLog(object sender, RoutedEventArgs e) => _sink?.Clear();
}