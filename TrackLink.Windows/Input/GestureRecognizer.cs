using TrackLink.Windows.Settings;

namespace TrackLink.Windows.Input;

/// <summary>
/// 把触控板的 HID 报文聚合成「一帧手势」，再判定成控制命令。
///
/// 帧边界（第 3 周用真机报文实测校正）：
/// <c>ContactCount</c> 是**采样**边界而不是手势边界——单指触摸时每条报文的 CC 都是 1，
/// 只有「同一采样内的后续接触点」报文才是 0（双指时同一 ScanTime 下发两条：CC=2 带 CID=0，CC=0 带 CID=1）。
/// 因此帧的定义是：**出现按下状态的接触点即开帧，按 ContactID 累积各接触点，直到全部 TipSwitch 释放才收帧**。
///
/// 单指按住期间还会持续发出 <c>MOVE</c> 帧，按**位移增量**驱动手机端光标（绝对映射会让手指
/// 抬起再落下时光标瞬移）；增量只累加不丢弃，节流见 <see cref="MoveIntervalMs"/>，
/// 灵敏度与边缘加速、反向滚动由 <see cref="GestureOptions"/> 提供（设置界面可调，改动即时生效）。
/// MOVE 是连续位置流，与手势判定互不干扰，也不占用冷却。
///
/// **跨帧状态机（长按 / 拖动）**：一次轻点抬起后 <see cref="DoubleTapWindowMs"/> 之内再次按下，
/// 就认为「双击后没松手」，立刻发 <c>DRAG_BEGIN</c> 让接收端真实按下；期间位移改发 <c>DRAG_MOVE</c>，
/// 抬手发 <c>DRAG_END</c>。按住不动由接收端的长按判定弹出菜单，按住后滑动即拖动——
/// 两种行为都由接收端按「按下的时长与位移」自行决定，本端不区分。
///
/// 判定阈值：单指轻点（位移 &lt;8% 且 &lt;450 ms）、双指上下滑（主轴为纵向且 &gt;10%）、
/// 三指轻点（同上轻点阈值 → 返回桌面）、三指横滑（横向 |位移| &gt;15%，左右都算 → 返回上一页）、
/// 物理按键置位；判定成功后 200 ms 冷却。
/// </summary>
internal sealed class GestureRecognizer
{
    private const double TapMoveRatio = 0.08;

    /// <summary>轻点最长按压时间。计划书原定 300 ms，实测本机轻点普遍 370~450 ms，故放宽。</summary>
    private const long TapMaxDurationMs = 450;
    private const double ScrollMinRatio = 0.10;
    private const double BackMinRatio = 0.15;
    private const long CooldownMs = 200;

    /// <summary>帧内超过这么久没有新报文，就认为抬指报文丢了，兜底收帧。</summary>
    private const long FrameIdleTimeoutMs = 300;

    /// <summary>光标位移（MOVE 帧）的最小发送间隔。触控板报文约 60~80 Hz，链路往返 16~31 ms，40 Hz 足够跟手。</summary>
    private const long MoveIntervalMs = 25;

    /// <summary>攒够多少归一化位移（32767 的约 0.2%，1080p 上约 2 px）才发一帧，兼作防抖死区。</summary>
    private const int MoveMinStep = 64;

    /// <summary>双击窗口：上一次轻点抬起后这么久之内再次按下，就认为「双击后没松手」。</summary>
    private const long DoubleTapWindowMs = 300;

    private sealed class Contact
    {
        public int StartX;
        public int StartY;
        public int LastX;
        public int LastY;
        public bool Tip;
    }

    private readonly (int Min, int Max) _xRange;
    private readonly (int Min, int Max) _yRange;

    /// <summary>可调手势参数（灵敏度 / 边缘加速 / 反向滚动）。每次使用时现读，设置改动即时生效。</summary>
    private readonly GestureOptions _options;

    private readonly Dictionary<int, Contact> _contacts = [];

    private bool _frameActive;
    private long _frameStartMs;
    private long _lastReportMs;
    private int _frameMaxContacts;
    private int _lastTipX = -1;
    private int _lastTipY = -1;
    private bool _buttonInFrame;
    private bool _buttonDown;
    private int _buttonX = -1;
    private int _buttonY = -1;
    private long _cooldownUntilMs;

    private long _lastMoveMs;

    /// <summary>本帧是否已取到基准点。开帧首条报文只做基准，不产生位移，否则会跨帧跳变。</summary>
    private bool _hasMoveOrigin;
    private int _movePrevX;
    private int _movePrevY;

    /// <summary>已累加但还没发出去的位移（归一化 i16 单位）。</summary>
    private int _pendingDx;
    private int _pendingDy;

    /// <summary>上一次轻点抬起的时刻；0 表示双击窗口已关闭。</summary>
    private long _lastTapReleaseMs;

    /// <summary>是否处于「长按 / 拖动」状态：接收端此刻有一个手指真实按在屏幕上。</summary>
    private bool _dragActive;

    public GestureRecognizer((int Min, int Max) xRange, (int Min, int Max) yRange, GestureOptions options)
    {
        _xRange = xRange;
        _yRange = yRange;
        _options = options;
    }

    /// <summary>识别出的一条命令。</summary>
    public event Action<RemoteCommand>? Command;

    /// <summary>每帧的小结日志，用于区分「手势判定错」与「协议/执行错」。</summary>
    public event Action<string>? Log;

    public void OnReport(TouchContactReport report)
    {
        var now = Environment.TickCount64;

        // 手指按住期间触控板会持续上报（ScanTime 一直推进），长时间收不到报文说明手指已离开；
        // 抬指报文偶有丢失，这里兜底收帧，避免把下一次触摸并进上一帧。
        if (_frameActive && now - _lastReportMs > FrameIdleTimeoutMs)
        {
            FinishFrame($"等待抬指报文超过 {FrameIdleTimeoutMs} ms", suppress: true);
        }

        _lastReportMs = now;
        HandleButton(report);

        // 开帧条件：出现「按下状态」的接触点。
        // 不能用 ContactCount>0 判帧——实测单指触摸时每条报文的 CC 都是 1，它标的是采样边界而非手势边界。
        if (!_frameActive)
        {
            if (!report.Contacts.Any(c => c.Tip != 0))
            {
                return;
            }

            _frameActive = true;
            _frameStartMs = now;
            _frameMaxContacts = 0;
            _lastTipX = -1;
            _lastTipY = -1;
            _buttonInFrame = _buttonDown;
            _hasMoveOrigin = false;
            _contacts.Clear();

            // 双击后不松手：上一帧刚轻点过、本帧又在双击窗口内按下 → 进入长按/拖动。
            // 第一下的点击已经照常发出去了——按下第二下之前，电脑不可能知道后面还有第二下。
            var sinceTap = now - _lastTapReleaseMs;
            if (!_buttonInFrame && _lastTapReleaseMs > 0 && sinceTap <= DoubleTapWindowMs)
            {
                var head = report.Contacts.First(c => c.Tip != 0);
                var x = Normalize(head.X, _xRange);
                var y = Normalize(head.Y, _yRange);
                _dragActive = true;
                Log?.Invoke($"··· 双击后未松手（距上次轻点 {sinceTap} ms）→ 进入长按/拖动");
                Command?.Invoke(RemoteCommand.DragBegin(x, y));
            }

            _lastTapReleaseMs = 0;
        }

        foreach (var point in report.Contacts)
        {
            var pressed = point.Tip != 0;

            if (_contacts.TryGetValue(point.Id, out var contact))
            {
                contact.Tip = pressed;

                // 抬指报文里的坐标不可信（常为 0），只在按下时更新位置。
                if (pressed)
                {
                    contact.LastX = point.X;
                    contact.LastY = point.Y;
                }
            }
            else
            {
                _contacts[point.Id] = new Contact
                {
                    StartX = point.X,
                    StartY = point.Y,
                    LastX = point.X,
                    LastY = point.Y,
                    Tip = pressed,
                };
            }

            if (pressed)
            {
                _lastTipX = point.X;
                _lastTipY = point.Y;
            }
        }

        var pressedCount = _contacts.Values.Count(c => c.Tip);

        // ContactCount 在「同一采样的首条报文」里给出本帧的接触点总数，作为指数的补充依据。
        _frameMaxContacts = Math.Max(_frameMaxContacts, Math.Max(pressedCount, report.ContactCount));

        if (pressedCount == 0)
        {
            FinishFrame(null, suppress: false);
            return;
        }

        // 拖动中又落了第二根手指：这不是拖动意图，立刻松手并放弃本帧。
        if (_dragActive && _frameMaxContacts >= 2)
        {
            EndDrag("拖动中落下第二根手指");
            FinishFrame("拖动中落下第二根手指", suppress: true);
            return;
        }

        // 单指按住期间持续累加位移，驱动手机端光标。
        // 本帧一旦出现过多指（滚动/返回），就不再移动光标，避免抬手瞬间光标乱跳。
        if (pressedCount == 1 && _frameMaxContacts <= 1)
        {
            var contact = _contacts.Values.First(c => c.Tip);
            AccumulateMove(contact.LastX, contact.LastY, now);
        }
    }

    /// <summary>
    /// 累加单指位移。MOVE 是**位移增量**：丢一段就永久少走一段，光标会漂移，
    /// 因此这里只累加、从不丢弃——攒够时间与距离再一次性发出去。
    /// 绝对值模式下丢包无所谓（下次重发位置），相对模式下不行。
    /// </summary>
    private void AccumulateMove(int rawX, int rawY, long now)
    {
        if (!_hasMoveOrigin)
        {
            // 开帧首条报文只当基准点：若拿上一帧手指的位置作起点，会产生一次大跳。
            _hasMoveOrigin = true;
            _movePrevX = rawX;
            _movePrevY = rawY;
            return;
        }

        _pendingDx += ScaleDelta(rawX - _movePrevX, _xRange, EdgeFactor(rawX, rawY));
        _pendingDy += ScaleDelta(rawY - _movePrevY, _yRange, EdgeFactor(rawX, rawY));
        _movePrevX = rawX;
        _movePrevY = rawY;

        if (now - _lastMoveMs < MoveIntervalMs)
        {
            return;
        }

        if (Math.Abs(_pendingDx) < MoveMinStep && Math.Abs(_pendingDy) < MoveMinStep)
        {
            return;
        }

        FlushMove();
    }

    /// <summary>把攒下的位移发出去。收帧时也调用，否则最后几个单位会卡在缓冲里直到下次触摸。</summary>
    private void FlushMove()
    {
        if (_pendingDx == 0 && _pendingDy == 0)
        {
            return;
        }

        var dx = Math.Clamp(_pendingDx, -short.MaxValue, short.MaxValue);
        var dy = Math.Clamp(_pendingDy, -short.MaxValue, short.MaxValue);

        // 超出 i16 的部分留到下一轮补发，保证累计位移不丢。
        _pendingDx -= dx;
        _pendingDy -= dy;
        _lastMoveMs = Environment.TickCount64;

        // 拖动中位移要同时驱动手机上的手指，与纯移动光标的帧类型不同。
        Command?.Invoke(_dragActive ? RemoteCommand.DragMove(dx, dy) : RemoteCommand.Move(dx, dy));
    }

    /// <summary>结束长按/拖动：先补发欠下的位移，再通知接收端抬手。</summary>
    private void EndDrag(string reason)
    {
        FlushMove();
        _dragActive = false;
        Log?.Invoke($"··· 长按/拖动结束（{reason}）");
        Command?.Invoke(RemoteCommand.DragEnd());
    }

    /// <summary>
    /// 边缘加速：手指落在触控板边缘区时把位移放大，弥补边缘处手指行程不足。
    /// 关闭时恒为 1。
    /// </summary>
    private double EdgeFactor(int rawX, int rawY)
    {
        if (!_options.EdgeAccelEnabled)
        {
            return 1.0;
        }

        var zone = _options.EdgeZoneRatio;
        var nx = Ratio(rawX, _xRange);
        var ny = Ratio(rawY, _yRange);

        return nx < zone || nx > 1 - zone || ny < zone || ny > 1 - zone
            ? _options.EdgeAccelFactor
            : 1.0;
    }

    /// <summary>触控板逻辑坐标 → 0..1 的相对位置（越界时截断）。</summary>
    private static double Ratio(int value, (int Min, int Max) range)
    {
        var span = range.Max - range.Min;
        if (span <= 0)
        {
            return 0.5;
        }

        return Math.Clamp((value - range.Min) / (double)span, 0, 1);
    }

    /// <summary>单轴位移：触控板原始单位 → 归一化 i16 增量（±32767 ≈ 一屏）。</summary>
    private int ScaleDelta(int rawDelta, (int Min, int Max) range, double edgeFactor)
    {
        var span = range.Max - range.Min;
        if (span <= 0)
        {
            return 0;
        }

        return (int)Math.Round(rawDelta / (double)span * _options.MoveGain * edgeFactor * 32767);
    }

    private void FinishFrame(string? reason, bool suppress)
    {
        _frameActive = false;

        // 拖动帧不走常规判定：抬手即结束长按/拖动，由接收端自己决定这是长按还是拖动。
        if (_dragActive)
        {
            EndDrag("抬手");
            _contacts.Clear();
            return;
        }

        // 收帧时把欠的位移补发出去，否则最后几个单位要等到下次触摸才生效。
        FlushMove();

        if (_contacts.Count == 0)
        {
            _contacts.Clear();
            return;
        }

        // 双击窗口只对「紧挨着的下一次按下」有效：任何一次非轻点的收帧都把它关掉。
        _lastTapReleaseMs = 0;

        var durationMs = Environment.TickCount64 - _frameStartMs;
        var maxContacts = _frameMaxContacts;
        var dx = _contacts.Values.Average(c => c.LastX - c.StartX);
        var dy = _contacts.Values.Average(c => c.LastY - c.StartY);
        var dxRatio = dx / (_xRange.Max - _xRange.Min);
        var dyRatio = dy / (_yRange.Max - _yRange.Min);
        var summary = $"帧：接触点={maxContacts} 时长={durationMs}ms 位移=({dxRatio:P0}, {dyRatio:P0})";
        var buttonInFrame = _buttonInFrame;
        _contacts.Clear();

        if (suppress)
        {
            Log?.Invoke($"··· {summary} → 忽略（{reason}）");
            return;
        }

        if (buttonInFrame)
        {
            Log?.Invoke($"··· {summary} → 忽略（该帧含物理按键，已单独发出 TAP）");
            return;
        }

        var now = Environment.TickCount64;
        if (now < _cooldownUntilMs)
        {
            Log?.Invoke($"··· {summary} → 忽略（冷却中，剩余 {_cooldownUntilMs - now} ms）");
            return;
        }

        // 三指：轻点 = 返回桌面，横滑（左右都算）= 返回上一页。
        if (maxContacts >= 3)
        {
            if (Math.Abs(dxRatio) < TapMoveRatio
                && Math.Abs(dyRatio) < TapMoveRatio
                && durationMs < TapMaxDurationMs)
            {
                Emit(RemoteCommand.Global(GlobalAction.Home), summary);
                return;
            }

            if (Math.Abs(dxRatio) >= BackMinRatio)
            {
                Emit(RemoteCommand.Global(GlobalAction.Back), summary);
                return;
            }
        }

        if (maxContacts == 2 && Math.Abs(dyRatio) > Math.Abs(dxRatio) && Math.Abs(dyRatio) > ScrollMinRatio)
        {
            var scrollDx = (int)Math.Round(Math.Clamp(dxRatio, -1, 1) * 32767);
            var scrollDy = (int)Math.Round(Math.Clamp(dyRatio, -1, 1) * 32767);

            // 反向滚动：只翻纵向，横向不动（双指纵向滑动才是本分支判定依据）。
            if (_options.ReverseScroll)
            {
                scrollDy = -scrollDy;
            }

            Emit(RemoteCommand.Scroll(scrollDx, scrollDy), summary);
            return;
        }

        if (maxContacts == 1
            && Math.Abs(dxRatio) < TapMoveRatio
            && Math.Abs(dyRatio) < TapMoveRatio
            && durationMs < TapMaxDurationMs)
        {
            // 坐标只是「光标尚未出现」时的回退值：光标一旦显示，Android 端会改点光标当前位置。
            var x = _lastTipX >= 0 ? Normalize(_lastTipX, _xRange) : 32767;
            var y = _lastTipY >= 0 ? Normalize(_lastTipY, _yRange) : 32767;

            // 记下抬起时刻：300 ms 内再次按下就当作「双击后没松手」。
            _lastTapReleaseMs = Environment.TickCount64;
            Emit(RemoteCommand.Tap(x, y), summary);
            return;
        }

        Log?.Invoke($"··· {summary} → 未识别");
    }

    private void HandleButton(TouchContactReport report)
    {
        if (report.Button)
        {
            if (!_buttonDown)
            {
                _buttonDown = true;
                _buttonInFrame = true;

                // 按下时刻的接触点位置；没有接触点时留 -1，抬起时改用屏幕中心。
                _buttonX = _lastTipX;
                _buttonY = _lastTipY;
            }

            return;
        }

        if (!_buttonDown)
        {
            return;
        }

        _buttonDown = false;
        var x = _buttonX >= 0 ? Normalize(_buttonX, _xRange) : 32767;
        var y = _buttonY >= 0 ? Normalize(_buttonY, _yRange) : 32767;
        Emit(RemoteCommand.Tap(x, y), _buttonX >= 0 ? "物理按键（用按下时的接触点位置）" : "物理按键（无接触点，用屏幕中心）");
    }

    private void Emit(RemoteCommand command, string summary)
    {
        _cooldownUntilMs = Environment.TickCount64 + CooldownMs;
        Log?.Invoke($"{summary} → 识别为 {command.Describe()}");
        Command?.Invoke(command);
    }

    /// <summary>触控板逻辑坐标 → 归一化 0..65535（与 Android 端的屏幕映射基准一致）。</summary>
    private static int Normalize(int value, (int Min, int Max) range)
    {
        var span = range.Max - range.Min;
        if (span <= 0)
        {
            return 32767;
        }

        var clamped = Math.Clamp(value, range.Min, range.Max);
        return (int)Math.Round((clamped - range.Min) * 65535.0 / span);
    }
}