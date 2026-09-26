namespace TrackLink.Windows.Settings;

/// <summary>
/// 可调手势参数（计划书「手势与坐标策略」要求的灵敏度、边缘加速、反向滚动、安全停止热键）。
///
/// <para>
/// 这份对象被设置界面与手势线程共同持有：设置界面改字段，手势线程**每次使用时现读**，
/// 不缓存副本，因此改动即时生效，不需要重启监听。
/// </para>
/// </summary>
internal sealed class GestureOptions
{
    /// <summary>触控板走满一个轴时手机光标走几屏。默认 2.5。</summary>
    public double MoveGain { get; set; } = DefaultMoveGain;

    /// <summary>是否开启边缘加速：手指落在触控板边缘区时位移按系数放大，方便跨屏。</summary>
    public bool EdgeAccelEnabled { get; set; } = DefaultEdgeAccelEnabled;

    /// <summary>边缘加速的放大系数。</summary>
    public double EdgeAccelFactor { get; set; } = DefaultEdgeAccelFactor;

    /// <summary>边缘区宽度占整个轴的比例（两端各这么多）。</summary>
    public double EdgeZoneRatio { get; set; } = DefaultEdgeZoneRatio;

    /// <summary>反向滚动：双指上滑变成向下滚。</summary>
    public bool ReverseScroll { get; set; }

    /// <summary>安全停止热键，触发即暂停控制。取值见 <see cref="HotkeyNames"/>。</summary>
    public string StopHotkey { get; set; } = DefaultStopHotkey;

    public const double DefaultMoveGain = 2.5;
    public const bool DefaultEdgeAccelEnabled = true;
    public const double DefaultEdgeAccelFactor = 1.5;
    public const double DefaultEdgeZoneRatio = 0.15;
    public const bool DefaultReverseScroll = false;
    public const string DefaultStopHotkey = "Ctrl+Alt+Q";

    /// <summary>可选的停止热键名称（供设置界面下拉）。</summary>
    public static readonly string[] HotkeyNames = ["Ctrl+Alt+Q", "Ctrl+Alt+F12", "Ctrl+Shift+F12", "禁用"];

    public void Reset()
    {
        MoveGain = DefaultMoveGain;
        EdgeAccelEnabled = DefaultEdgeAccelEnabled;
        EdgeAccelFactor = DefaultEdgeAccelFactor;
        EdgeZoneRatio = DefaultEdgeZoneRatio;
        ReverseScroll = DefaultReverseScroll;
        StopHotkey = DefaultStopHotkey;
    }
}