using TrackLink.Windows.Bluetooth;
using TrackLink.Windows.Input;
using TrackLink.Windows.Settings;

namespace TrackLink.Windows.UI;

/// <summary>
/// 链路阶段。界面用它决定显示哪张表情贴纸——尤其是把「心跳超时」与「对端正常断开」分开，
/// 这两者在旧的文案里都是「等待手机连接」，肉眼分不出来。
/// </summary>
internal enum LinkPhase
{
    /// <summary>服务未启动。</summary>
    Stopped,

    /// <summary>监听中，等待手机连接。</summary>
    Waiting,

    /// <summary>已连接。</summary>
    Connected,

    /// <summary>上一次会话因心跳超时断开。</summary>
    Timeout,

    /// <summary>上一次会话因对端断开 / 接收错误结束。</summary>
    Lost,
}

/// <summary>
/// 图形界面的后台编排：把蓝牙服务端、触控板监听、手势识别、本机鼠标闸门串成一条链路。
///
/// 线程约定：<see cref="Start"/> / <see cref="Stop"/> / <see cref="SelectTouchpad"/> 只在 UI 线程调用；
/// 命令与日志来自后台线程，界面用 <c>DispatcherTimer</c> 轮询这里的快照属性，
/// 只有「日志」与「会话变化」需要编组（由 <see cref="UiLogSink"/> 负责）。
/// </summary>
internal sealed class RemoteEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly RfcommHost _host = new();

    private RawInputListener? _listener;
    private GestureRecognizer? _recognizer;
    private Action<TouchContactReport>? _recognizerHandler;
    private Action<TouchContactReport>? _blockerHandler;
    private BtSession? _rttSession;
    private int _disposed;
    private volatile bool _hadSession;

    private volatile bool _running;
    private volatile bool _paused;
    private volatile bool _mouseBlockEnabled = true;
    private int _phase = (int)LinkPhase.Stopped;
    private long _rttMs = -1;
    private string _connectionText = "未启动";
    private string _remoteText = "—";
    private string _lastGestureText = "（暂无）";
    private string _touchpadText = "（未选定触控板）";

    public RemoteEngine(AppSettings settings)
    {
        _settings = settings;

        _host.Log += line => Log?.Invoke(line);
        _host.SessionChanged += OnSessionChanged;
    }

    /// <summary>来自后台线程的日志行；由界面接到 <see cref="UiLogSink"/>。</summary>
    public event Action<string>? Log;

    /// <summary>服务端是否已启动（监听中）。</summary>
    public bool IsRunning => _running;

    /// <summary>暂停控制：为 true 时识别出的命令一律丢弃。</summary>
    public bool IsPaused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>是否屏蔽触控板在本机的副作用（不带动本机光标、不在本机点击）。</summary>
    public bool MouseBlockEnabled
    {
        get => _mouseBlockEnabled;
        set
        {
            _mouseBlockEnabled = value;
            ApplyMouseBlock();
        }
    }

    /// <summary>最近一次心跳往返毫秒数；尚未收到 PONG 时为 null。</summary>
    public long? RttMs => _rttMs >= 0 ? _rttMs : null;

    /// <summary>服务状态文案（本机地址 / 通道 / SDP 结果）。</summary>
    public string ServiceText { get; private set; } = "服务未启动";

    /// <summary>连接状态文案。</summary>
    public string ConnectionText => _connectionText;

    /// <summary>当前链路阶段（界面据此选贴纸）。</summary>
    public LinkPhase Phase => (LinkPhase)Volatile.Read(ref _phase);

    /// <summary>本次运行是否成功建立过至少一次会话（用于区分「从未连上」与「连过又断了」）。</summary>
    public bool HadSession => _hadSession;

    /// <summary>远端设备地址文案。</summary>
    public string RemoteText => _remoteText;

    /// <summary>最近一次识别出的手势文案。</summary>
    public string LastGestureText => _lastGestureText;

    /// <summary>已选定触控板的摘要（分组键、归一化范围、范围来源）。</summary>
    public string TouchpadText => _touchpadText;

    /// <summary>未能自动选定触控板时的原因文案，供设备选择页直接显示。</summary>
    public string? SelectionProblem { get; private set; }

    /// <summary>Raw Input 监听启动失败的原因；正常时为 null。</summary>
    public string? ListenerProblem { get; private set; }

    /// <summary>当前候选触控板（Raw Input 0x0D/0x05 集合）。</summary>
    public TouchpadDevice[] Candidates => _listener?.Candidates ?? [];

    /// <summary>是否发现任何触控板多触点集合。全部候选都没有时界面要明确提示「不支持仅触控板过滤」。</summary>
    public bool HasTouchpad => Candidates.Length > 0;

    /// <summary>Raw Input 监听是否真的在跑。为 false 时一条报文都收不到，界面必须显式提示而不是干等。</summary>
    public bool ListenerStarted => _listener?.Started == true;

    /// <summary>本次运行累计收到的触控板 WM_INPUT 条数，用于区分「没收到输入」与「收到但没识别」。</summary>
    public long InputMessageCount => _listener?.InputMessageCount ?? 0;

    /// <summary>已选定的触控板分组键；未选定时为 null。</summary>
    public string? SelectedGroupKey => _listener?.SelectedGroupKey;

    /// <summary>启动服务端与触控板监听。触控板尚未选定时只做活动检测，不转发任何手势。</summary>
    public bool Start()
    {
        if (_running)
        {
            return true;
        }

        if (!_host.Start())
        {
            ServiceText = "服务启动失败（请确认蓝牙已开启）";
            SelectionProblem = "蓝牙服务未启动，触控板监听未开始。请确认蓝牙已开启后重试。";
            Log?.Invoke("[服务] ✗ 蓝牙服务端启动失败。");
            return false;
        }

        ServiceText = $"本机地址 {Bt.FormatAddress(_host.LocalAddress)} · RFCOMM 通道 {_host.Channel} · "
                      + $"SDP 注册 {(_host.ServiceRegistered ? "成功" : "失败")}";
        _connectionText = "等待手机连接";
        _hadSession = false;
        SetPhase(LinkPhase.Waiting);

        _listener = new RawInputListener();
        _listener.Log += line => Log?.Invoke(line);
        if (!_listener.Start())
        {
            ListenerProblem = "触控板 Raw Input 监听启动失败，收不到任何触控板输入。"
                              + "原因见「② 连接与控制」的运行日志；重启程序通常可恢复。";
            Log?.Invoke($"[输入] ✗ 触控板监听启动失败：{ListenerProblem}");
        }
        else
        {
            ListenerProblem = null;
        }

        _running = true;
        ApplyMouseBlock();

        // 已保存过指纹时直接恢复选择：重启服务后不必再选一次触控板。
        SelectionProblem = null;
        var saved = _settings.TouchpadGroupKey;
        if (string.IsNullOrEmpty(saved))
        {
            SelectionProblem = "首次使用：请在触控板上单指来回滑动 3 秒完成检测，然后点「就用它」。";
        }
        else if (!SelectTouchpad(saved, out var error))
        {
            SelectionProblem = $"上次选定的触控板未找到，请重新选择。（{error}）";
        }

        // 监听器没起来时优先报这个：此时无论怎么滑动触控板都不会有任何报文，选不选设备都没意义。
        if (ListenerProblem is { } listenerProblem)
        {
            SelectionProblem = listenerProblem;
        }

        Log?.Invoke("[服务] 已启动，等待手机连接。");
        return true;
    }

    /// <summary>停止服务与监听，并摘掉鼠标闸门。</summary>
    public void Stop()
    {
        if (!_running)
        {
            return;
        }

        ClearTouchpad();

        _listener?.Dispose();
        _listener = null;

        _host.Stop();

        _running = false;
        ServiceText = "服务未启动";
        _connectionText = "未启动";
        _remoteText = "—";
        _rttMs = -1;
        _hadSession = false;
        SetPhase(LinkPhase.Stopped);
        SelectionProblem = null;
        ListenerProblem = null;

        LocalMouseBlocker.Stop();
        Log?.Invoke("[服务] 已停止。");
    }

    /// <summary>断开当前会话（服务端继续监听，手机可重连）。</summary>
    public void DisconnectSession()
    {
        var session = _host.CurrentSession;
        if (session is null)
        {
            Log?.Invoke("[连接] 当前没有活动会话。");
            return;
        }

        Log?.Invoke("[连接] 正在断开当前会话…");
        session.Dispose();
    }

    /// <summary>
    /// 选定触控板：只为该分组键的设备建立手势识别。
    /// 工厂侧的原始 HID 报文由 <see cref="RawInputListener"/> 按分组键过滤，别的触控板一律不转发。
    /// </summary>
    public bool SelectTouchpad(string groupKey, out string? error)
    {
        error = null;

        var listener = _listener;
        if (listener is null)
        {
            error = "服务尚未启动";
            return false;
        }

        // 监听器没起来时选定设备是假象：之后一条报文都不会来，不如直接报出真实原因。
        if (!listener.Started)
        {
            error = ListenerProblem ?? "触控板监听未启动";
            return false;
        }

        var device = listener.FindByGroupKey(groupKey);
        if (device is null)
        {
            error = "该设备已不在候选列表中，请重新检测";
            return false;
        }

        if (device.Decoder is null || !device.Decoder.Ready)
        {
            error = $"该触控板的多触点集合无法解码：{device.Decoder?.Error ?? "未知原因"}";
            return false;
        }

        ClearTouchpad();

        _recognizer = new GestureRecognizer(device.Decoder.XRange, device.Decoder.YRange, _settings.Gesture);
        _recognizer.Log += line => Log?.Invoke(line);
        _recognizer.Command += OnCommand;

        _recognizerHandler = _recognizer.OnReport;
        _blockerHandler = _ => LocalMouseBlocker.NoteTouchpadReport();
        listener.Report += _recognizerHandler;
        listener.Report += _blockerHandler;

        // 最后再设分组键：事件先接好，避免刚选定就漏掉前几条报文。
        listener.SelectedGroupKey = groupKey;

        var source = device.Decoder.UsingFallbackRanges ? "第 1 周实测回退值" : "能力表 LogicalMin/Max";
        _touchpadText = $"{device.DisplayName}（分组键 {groupKey}）"
                        + $"\n归一化范围 X {device.Decoder.XRange.Min}..{device.Decoder.XRange.Max}"
                        + $" / Y {device.Decoder.YRange.Min}..{device.Decoder.YRange.Max}（来源：{source}）"
                        + $"\n设备路径 {device.Path}";

        Log?.Invoke($"[输入] 已选定触控板 {device.DisplayName}（分组键 {groupKey}），手势转发已开启。");
        return true;
    }

    /// <summary>解绑手势识别（换设备或停止服务时调用）。</summary>
    public void ClearTouchpad()
    {
        var listener = _listener;
        if (listener is not null)
        {
            if (_recognizerHandler is not null)
            {
                listener.Report -= _recognizerHandler;
            }

            if (_blockerHandler is not null)
            {
                listener.Report -= _blockerHandler;
            }

            listener.SelectedGroupKey = null;
        }

        if (_recognizer is not null)
        {
            _recognizer.Command -= OnCommand;
            _recognizer = null;
        }

        _recognizerHandler = null;
        _blockerHandler = null;
        _touchpadText = "（未选定触控板）";
    }

    /// <summary>开始一次 3 秒活动统计窗口（设备选择页用）。</summary>
    public void BeginActivityWindow() => _listener?.BeginActivityWindow();

    /// <summary>结束活动统计窗口。</summary>
    public void EndActivityWindow() => _listener?.EndActivityWindow();

    /// <summary>活动窗口内该设备的报文条数。</summary>
    public int WindowReportCount(IntPtr handle) => _listener?.WindowReportCount(handle) ?? 0;

    /// <summary>该设备最近一次上报距今毫秒数；从未上报过为 null。</summary>
    public long? ActivityAgeMs(IntPtr handle) => _listener?.ActivityAgeMs(handle);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            ClearTouchpad();
            _listener?.Dispose();
            _listener = null;

            _host.SessionChanged -= OnSessionChanged;
            DetachRtt();
            _host.Dispose();
        }
        finally
        {
            // 无论清理中途出什么问题，鼠标钩子都必须摘掉，否则本机光标行为会一直异常。
            LocalMouseBlocker.Stop();
        }
    }

    private void ApplyMouseBlock()
    {
        if (!_running || !_mouseBlockEnabled)
        {
            LocalMouseBlocker.Stop();
            return;
        }

        Log?.Invoke(LocalMouseBlocker.Start(out var error)
            ? "[触控板] 本机鼠标闸门已开启：触控板不再带动本机光标、也不在本机点击（外接鼠标不受影响）。"
            : $"[触控板] ✗ 本机鼠标闸门开启失败（{error}），本机光标仍会跟着手指动。");
    }

    private void OnSessionChanged(BtSession? session)
    {
        DetachRtt();

        if (session is null)
        {
            _connectionText = _running ? "等待手机连接" : "未启动";
            _remoteText = "—";
            _rttMs = -1;

            // 会话在这里已经被清成 null，结束原因只能从 RfcommHost 里取：
            // 超时（3 秒没有任何帧）与对端主动断开要给用户看完全不同的表情与提示。
            var timedOut = _host.LastSessionTimedOut;
            SetPhase(_running ? (timedOut ? LinkPhase.Timeout : LinkPhase.Lost) : LinkPhase.Stopped);

            Log?.Invoke(timedOut
                ? "[连接] 心跳超时（3 秒内未收到任何帧），链路已断开；等待手机重连。"
                : "[连接] 手机已断开，手势命令将被丢弃。");
            return;
        }

        session.Rtt += OnRtt;
        _rttSession = session;
        _connectionText = "已连接";
        _remoteText = Bt.FormatAddress(session.RemoteAddress);
        _hadSession = true;
        SetPhase(LinkPhase.Connected);
        Log?.Invoke($"[连接] 手机已连接（{_remoteText}），开始转发手势命令。");
    }

    private void SetPhase(LinkPhase phase) => Volatile.Write(ref _phase, (int)phase);

    private void DetachRtt()
    {
        if (_rttSession is null)
        {
            return;
        }

        // ReferenceEquals 防竞态：解绑的一定是当前这条会话。
        if (ReferenceEquals(_host.CurrentSession, _rttSession))
        {
            _rttSession.Rtt -= OnRtt;
        }

        _rttSession = null;
        _rttMs = -1;
    }

    private void OnRtt(long rtt) => _rttMs = rtt;

    private void OnCommand(RemoteCommand command)
    {
        // 连续位置流（MOVE / DRAG_MOVE）每秒几十条，逐条写日志会把手势小结刷掉。
        var quiet = command.Kind is RemoteCommandKind.Move or RemoteCommandKind.DragMove;
        _lastGestureText = command.Describe();

        if (_paused)
        {
            if (!quiet)
            {
                Log?.Invoke($"控制已暂停，丢弃 {command.Describe()}");
            }

            return;
        }

        var session = _host.CurrentSession;
        if (session is null)
        {
            if (!quiet)
            {
                Log?.Invoke($"无活动会话，丢弃 {command.Describe()}");
            }

            return;
        }

        if (quiet)
        {
            session.Send(command.Type, command.EncodePayload());
            return;
        }

        var sentAt = Environment.TickCount64;
        var ok = session.Send(command.Type, command.EncodePayload());
        Log?.Invoke($"[{sentAt} ms] 发送 {command.Describe()} → {(ok ? "成功" : "失败")}");
    }
}