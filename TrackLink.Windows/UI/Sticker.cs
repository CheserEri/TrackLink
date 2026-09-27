using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
// WinForms 目标框架会隐式引入 System.Drawing，那里也有 Image / Point，必须显式指定 WPF 的。
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace TrackLink.Windows.UI;

/// <summary>
/// 表情贴纸的语义键。界面只按<b>含义</b>指定键，不出现文件名——
/// 这样换图、换风格时只要改 <see cref="Sticker.FileOf"/> 一处。
/// </summary>
internal enum StickerKey
{
    /// <summary>启动欢迎（派蒙开心，动图）。</summary>
    Welcome,

    /// <summary>等待手机连接（双手合十祈祷）。</summary>
    Waiting,

    /// <summary>连接中（派蒙晕眩，动图）。</summary>
    Connecting,

    /// <summary>已连接成功（抱面包幸福）。</summary>
    Connected,

    /// <summary>服务已就绪 / 启动成功（举拳自信）。</summary>
    Ready,

    /// <summary>抓到了：检测到触控板（出击闪光）。</summary>
    Found,

    /// <summary>扫描 / 检测中（兰那罗飞行）。</summary>
    Searching,

    /// <summary>空列表 / 未发现设备（棕发蓝眼疑惑）。</summary>
    Nothing,

    /// <summary>已暂停（喝茶休息）。</summary>
    Paused,

    /// <summary>手势识别正常（闭眼哼歌）。</summary>
    Great,

    /// <summary>连接断开 / 连接失败（一人睡着、一人震惊）。</summary>
    Lost,

    /// <summary>心跳超时（用户指定的「麻烦」大图，唯一用途）。</summary>
    Timeout,

    /// <summary>权限 / 无障碍未启用（刻晴：我拒绝）。</summary>
    Rejected,

    /// <summary>提示 / 疑问（兰那罗疑问）。</summary>
    Hint,

    /// <summary>等待 / 处理中（七七输入中）。</summary>
    Busy,

    /// <summary>退出 / 已停止（北斗：拜拜）。</summary>
    Bye,

    /// <summary>异常警告（冰史莱姆惊，动图）。</summary>
    Shock,

    /// <summary>品牌图标（也用于窗口与托盘图标）。</summary>
    Brand,
}

/// <summary>
/// 一张表情贴纸：按语义键取内嵌图片显示，GIF 会逐帧播放，切换有「弹出 + 淡入」入场动画。
///
/// 直接继承 <see cref="Grid"/> 而不走 ControlTemplate：贴纸只是一张图加两个动画，
/// 做成模板反而要额外维护一份 <c>ControlTemplate</c> 资源，没有任何收益。
/// </summary>
internal sealed class Sticker : Grid
{
    private static readonly Dictionary<StickerKey, BitmapFrame[]> Cache = [];
    private static readonly object CacheLock = new();

    private readonly Image _image = new();
    private readonly DispatcherTimer _timer;
    private readonly TranslateTransform _float = new();

    private BitmapFrame[] _frames = [];
    private int _frameIndex;
    private bool _animating;

    public Sticker()
    {
        _image.Stretch = Stretch.Uniform;
        _image.SnapsToDevicePixels = true;
        // 显式给初值：Image 不给宽高时会撑成图片的自然像素尺寸（几百 px），把布局顶歪。
        _image.Width = 96;
        _image.Height = 96;
        // 文字意义上的「贴纸」不是可交互控件，别吃掉鼠标事件（卡片悬停效果要能透过来）。
        IsHitTestVisible = false;
        RenderTransform = _float;

        Children.Add(_image);

        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnFrameTick;
    }

    public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
        nameof(Key),
        typeof(StickerKey),
        typeof(Sticker),
        new PropertyMetadata(StickerKey.Nothing, OnKeyChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size),
        typeof(double),
        typeof(Sticker),
        new PropertyMetadata(96d, OnSizeChanged));

    public static readonly DependencyProperty FloatProperty = DependencyProperty.Register(
        nameof(Float),
        typeof(bool),
        typeof(Sticker),
        new PropertyMetadata(false, OnFloatChanged));

    /// <summary>要显示的表情。</summary>
    public StickerKey Key
    {
        get => (StickerKey)GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    /// <summary>边长（像素）。贴纸一律按正方形约束，换图不会撑动布局。</summary>
    public double Size
    {
        get => (double)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>是否做无限上下轻浮动（只给页面里最大的那张状态贴纸开）。</summary>
    public bool Float
    {
        get => (bool)GetValue(FloatProperty);
        set => SetValue(FloatProperty, value);
    }

    /// <summary>语义键 → 内嵌资源文件名。</summary>
    public static string FileOf(StickerKey key) => key switch
    {
        StickerKey.Welcome => "st_welcome.gif",
        StickerKey.Waiting => "st_waiting.png",
        StickerKey.Connecting => "st_connecting.gif",
        StickerKey.Connected => "st_connected.png",
        StickerKey.Ready => "st_ready.png",
        StickerKey.Found => "st_found.png",
        StickerKey.Searching => "st_searching.png",
        StickerKey.Nothing => "st_nothing.png",
        StickerKey.Paused => "st_paused.png",
        StickerKey.Great => "st_great.png",
        StickerKey.Lost => "st_lost.png",
        StickerKey.Timeout => "st_timeout.png",
        StickerKey.Rejected => "st_rejected.png",
        StickerKey.Hint => "st_hint.png",
        StickerKey.Busy => "st_busy.png",
        StickerKey.Bye => "st_bye.png",
        StickerKey.Shock => "st_shock.gif",
        StickerKey.Brand => "brand.png",
        _ => "st_nothing.png",
    };

    /// <summary>
    /// 读取内嵌贴纸的帧序列。GIF 得到多帧，PNG 得到 1 帧——
    /// 用 <see cref="BitmapDecoder.Create(Uri, BitmapCreateOptions, BitmapCacheOption)"/> 让
    /// WPF 自己挑解码器，不必按扩展名分支（否则 PNG 喂给 GifBitmapDecoder 会直接抛异常）。
    /// </summary>
    public static BitmapFrame[] LoadFrames(StickerKey key)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            BitmapFrame[] frames;
            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Stickers/{FileOf(key)}", UriKind.Absolute);
                var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                frames = [.. decoder.Frames];

                // 逐帧预冻结：动画每帧都会换 Source，冻结过的帧跨线程/跨渲染复用更省。
                foreach (var frame in frames.Where(f => f.CanFreeze && !f.IsFrozen))
                {
                    frame.Freeze();
                }
            }
            catch (Exception)
            {
                // 缺图不该让整个界面崩掉：退化成一张空帧，界面其余部分照常工作。
                frames = [];
            }

            Cache[key] = frames;
            return frames;
        }
    }

    /// <summary>第一帧位图，供窗口 / 托盘图标复用。</summary>
    public static BitmapFrame? FirstFrame(StickerKey key) => LoadFrames(key).FirstOrDefault();

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Sticker)d).Apply((StickerKey)e.NewValue);

    private static void OnSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var sticker = (Sticker)d;
        sticker._image.Width = (double)e.NewValue;
        sticker._image.Height = (double)e.NewValue;
    }

    private static void OnFloatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((Sticker)d).ApplyFloat((bool)e.NewValue);

    private void Apply(StickerKey key)
    {
        _timer.Stop();
        _animating = false;
        _frameIndex = 0;
        _frames = LoadFrames(key);

        if (_frames.Length == 0)
        {
            _image.Source = null;
            return;
        }

        _image.Source = _frames[0];

        if (_frames.Length > 1)
        {
            // GIF 帧间隔写在 /grctlext/Delay，单位 1/100 秒；缺元数据时按 100 ms 处理。
            _timer.Interval = FrameDelay(_frames[0]);
            _animating = true;
            _timer.Start();
        }

        PlayEntrance();
    }

    private void OnFrameTick(object? sender, EventArgs e)
    {
        if (!_animating || _frames.Length == 0)
        {
            return;
        }

        _frameIndex = (_frameIndex + 1) % _frames.Length;
        var frame = _frames[_frameIndex];
        _image.Source = frame;
        _timer.Interval = FrameDelay(frame);
    }

    private static TimeSpan FrameDelay(BitmapFrame frame)
    {
        var delay = 10;
        if (frame.Metadata is BitmapMetadata metadata && metadata.ContainsQuery("/grctlext/Delay"))
        {
            delay = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay"));
        }

        // 浏览器与系统查看器都把 0/1 这种极端值兜到 100 ms，否则会闪成一片白。
        var ms = delay < 2 ? 100 : delay * 10;
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary>入场：缩放从 0.82 弹到 1，同时淡入。</summary>
    private void PlayEntrance()
    {
        var scale = new ScaleTransform(0.82, 0.82);
        RenderTransformOrigin = new Point(0.5, 0.5);

        var target = _image;
        target.RenderTransform = scale;
        target.RenderTransformOrigin = new Point(0.5, 0.5);

        var easing = new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(380);

        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.82, 1.0, duration) { EasingFunction = easing });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.82, 1.0, duration) { EasingFunction = easing });
        target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
    }

    /// <summary>无限上下浮动：幅度 4 px、周期 2.6 s，用 Sine 缓动避免生硬折返。</summary>
    private void ApplyFloat(bool enabled)
    {
        if (!enabled)
        {
            _float.BeginAnimation(TranslateTransform.YProperty, null);
            _float.Y = 0;
            return;
        }

        var animation = new DoubleAnimation(-4, 4, TimeSpan.FromMilliseconds(1300))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        _float.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>给贴纸配一句人话说明，界面把它显示在贴纸下方（贴纸自身不吃命中测试，挂不了 ToolTip）。</summary>
    public static string DescribeOf(StickerKey key) => key switch
    {
        StickerKey.Welcome => "欢迎使用 TrackLink",
        StickerKey.Waiting => "等待手机连接",
        StickerKey.Connecting => "正在连接…",
        StickerKey.Connected => "已连接",
        StickerKey.Ready => "服务已就绪",
        StickerKey.Found => "已抓到触控板",
        StickerKey.Searching => "正在检测…",
        StickerKey.Nothing => "暂时什么都没有",
        StickerKey.Paused => "已暂停（喝口茶）",
        StickerKey.Great => "手势识别正常",
        StickerKey.Lost => "连接断开了",
        StickerKey.Timeout => "心跳超时：3 秒内没收到手机的任何回应",
        StickerKey.Rejected => "被拒绝了",
        StickerKey.Hint => "看一眼这里",
        StickerKey.Busy => "处理中…",
        StickerKey.Bye => "已停止",
        StickerKey.Shock => "出问题了！",
        StickerKey.Brand => "TrackLink",
        _ => "",
    };
}