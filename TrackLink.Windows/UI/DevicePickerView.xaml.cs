using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using TrackLink.Windows.Settings;

namespace TrackLink.Windows.UI;

/// <summary>设备选择页里的一行候选触控板。</summary>
internal sealed class DeviceRow : INotifyPropertyChanged
{
    private string _activity = "";
    private bool _highlighted;

    public required string GroupKey { get; init; }

    public required string Name { get; init; }

    public required string Detail { get; init; }

    /// <summary>最近活动文案，每次刷新都变。</summary>
    public string Activity
    {
        get => _activity;
        set
        {
            if (_activity == value)
            {
                return;
            }

            _activity = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Activity)));
        }
    }

    /// <summary>是否为检测窗口内活动最多的设备。</summary>
    public bool IsHighlighted
    {
        get => _highlighted;
        set
        {
            if (_highlighted == value)
            {
                return;
            }

            _highlighted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHighlighted)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 触控板设备选择页（计划书第 84-95 行）：候选列表 + 「滑动 3 秒」高亮确认 + 路径指纹持久化。
///
/// 只在<b>没有任何 0x0D/0x05 多触点集合</b>时给出「不支持仅触控板过滤」的提示并禁用启动，
/// 绝不靠 VID:PID 或名称去猜哪块是触控板。
/// </summary>
internal partial class DevicePickerView : System.Windows.Controls.UserControl
{
    private const int ScanWindowMs = 3000;

    private readonly ObservableCollection<DeviceRow> _rows = [];
    private readonly DispatcherTimer _scanTimer;

    private RemoteEngine? _engine;
    private AppSettings? _settings;
    private UiLogSink? _sink;
    private Action? _onSelected;

    private long _scanDeadline;
    private bool _scanning;
    private bool _scanMissed;
    private StickerKey _stickerKey = StickerKey.Welcome;

    public DevicePickerView()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _rows;

        _scanTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _scanTimer.Tick += OnScanTick;
    }

    public void Initialize(RemoteEngine engine, AppSettings settings, UiLogSink sink, Action onSelected)
    {
        _engine = engine;
        _settings = settings;
        _sink = sink;
        _onSelected = onSelected;
        Refresh();
    }

    /// <summary>按引擎快照刷新候选列表与活动时间（由 MainWindow 的定时器调用）。</summary>
    public void Refresh()
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        var candidates = engine.Candidates;
        if (candidates.Length == 0)
        {
            _rows.Clear();
            UnsupportedBorder.Visibility = Visibility.Visible;
            UnsupportedText.Text = "未发现任何触控板多触点集合（HID 用途页 0x0D / 用途 0x05）。"
                                   + "本机没有可识别的内置触控板，或系统未把它暴露为多触点设备——"
                                   + "此时无法把触控板输入与普通鼠标区分开，程序不会猜测来源，手势转发将不可用。";
            ScanButton.IsEnabled = false;
            UseButton.IsEnabled = false;
            // 监听器根本没起来是另一回事：那是程序故障，不是「本机没触控板」。
            SetSticker(engine.ListenerStarted ? StickerKey.Nothing : StickerKey.Shock);
            return;
        }

        UnsupportedBorder.Visibility = Visibility.Collapsed;
        ScanButton.IsEnabled = true;

        // 候选集合变化（热插拔）时重建行；顺序按设备路径稳定排序，避免刷新时跳动。
        var ordered = candidates.OrderBy(d => d.Path, StringComparer.Ordinal).ToArray();
        if (_rows.Count != ordered.Length || ordered.Where((d, i) => _rows[i].GroupKey != d.GroupKey).Any())
        {
            _rows.Clear();
            foreach (var device in ordered)
            {
                _rows.Add(new DeviceRow
                {
                    GroupKey = device.GroupKey,
                    Name = device.DisplayName,
                    Detail = $"分组键 {device.GroupKey}\n设备路径 {device.Path}",
                });
            }
        }

        foreach (var device in ordered)
        {
            var row = _rows.FirstOrDefault(r => r.GroupKey == device.GroupKey);
            if (row is null)
            {
                continue;
            }

            var age = engine.ActivityAgeMs(device.Handle);
            var scans = _scanning ? engine.WindowReportCount(device.Handle) : 0;

            row.Activity = age is { } ms
                ? $"最近活动：{ms} ms 前" + (_scanning ? $" · 本次检测 {scans} 条报文" : "")
                : "最近活动：尚未收到任何报文" + (_scanning ? $" · 本次检测 {scans} 条报文" : "");
        }

        UseButton.IsEnabled = DeviceList.SelectedItem is DeviceRow;
        SetSticker(ComputeSticker());
    }

    /// <summary>本页表情按「用户此刻最需要知道什么」排优先级。</summary>
    private StickerKey ComputeSticker()
    {
        if (_scanning)
        {
            return StickerKey.Searching;
        }

        if (_scanMissed)
        {
            // 3 秒一条报文都没有：疑问脸最贴切，同时页脚文字已经给出具体排查方向。
            return StickerKey.Hint;
        }

        if (_rows.Any(r => r.IsHighlighted))
        {
            return StickerKey.Found;
        }

        return _engine?.SelectedGroupKey is not null ? StickerKey.Ready : StickerKey.Welcome;
    }

    private void SetSticker(StickerKey key)
    {
        PageStickerCaption.Text = Sticker.DescribeOf(key);

        if (_stickerKey == key)
        {
            return;
        }

        _stickerKey = key;
        PageSticker.Key = key;
    }

    /// <summary>设置页脚提示（首次使用 / 上次设备未找到）。</summary>
    public void SetHint(string hint) => StatusText.Text = hint;

    private void OnScan(object sender, RoutedEventArgs e)
    {
        var engine = _engine;
        if (engine is null || _scanning)
        {
            return;
        }

        foreach (var row in _rows)
        {
            row.IsHighlighted = false;
        }

        _scanning = true;
        _scanMissed = false;
        _scanDeadline = Environment.TickCount64 + ScanWindowMs;
        engine.BeginActivityWindow();
        StatusText.Text = "检测中：请用单指在触控板上连续来回滑动 3 秒…";
        _sink?.Post("[设备选择] 开始 3 秒活动检测，请单指在触控板上滑动。");
        _scanTimer.Start();
    }

    private void OnScanTick(object? sender, EventArgs e)
    {
        if (Environment.TickCount64 < _scanDeadline)
        {
            Refresh();
            return;
        }

        _scanTimer.Stop();
        _scanning = false;

        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        engine.EndActivityWindow();

        var counts = _rows
            .Select(row => (Row: row, Device: engine.Candidates.FirstOrDefault(d => d.GroupKey == row.GroupKey)))
            .Where(pair => pair.Device is not null)
            .Select(pair => (pair.Row, Count: engine.WindowReportCount(pair.Device!.Handle)))
            .ToList();

        var bestCount = counts.Count > 0 ? counts.Max(pair => pair.Count) : 0;
        var bestRow = counts.FirstOrDefault(pair => pair.Count == bestCount && bestCount > 0).Row;

        Refresh();

        if (bestRow is null)
        {
            // 把监听状态一起报出来：否则「收不到报文」和「收到但没识别」在界面上长得一模一样，没法排查。
            var diagnosis = engine.ListenerStarted
                ? $"监听正常，本次运行累计收到 {engine.InputMessageCount} 条触控板报文。"
                  + "请确认手指确实在内置触控板上滑动后重试。"
                : engine.ListenerProblem ?? "触控板监听未启动。";

            StatusText.Text = $"3 秒内没有收到任何触控板报文。{diagnosis}";
            _sink?.Post($"[设备选择] 3 秒检测结束，未收到任何报文（监听已启动={engine.ListenerStarted}，"
                        + $"累计 WM_INPUT={engine.InputMessageCount}）。");
            _scanMissed = true;
            SetSticker(ComputeSticker());
            return;
        }

        bestRow.IsHighlighted = true;
        DeviceList.SelectedItem = bestRow;
        StatusText.Text = $"检测完成：高亮的是「{bestRow.Name}」（3 秒内 {bestCount} 条报文）。确认后点「就用它」。";
        _sink?.Post($"[设备选择] 3 秒检测结束，活动最多的是 {bestRow.Name}（{bestCount} 条报文）。");
        UseButton.IsEnabled = true;
    }

    private void OnUseSelected(object sender, RoutedEventArgs e)
    {
        var engine = _engine;
        var settings = _settings;
        if (engine is null || settings is null || DeviceList.SelectedItem is not DeviceRow row)
        {
            return;
        }

        if (!engine.SelectTouchpad(row.GroupKey, out var error))
        {
            StatusText.Text = $"✗ 选定失败：{error}";
            return;
        }

        var device = engine.Candidates.FirstOrDefault(d => d.GroupKey == row.GroupKey);
        settings.TouchpadGroupKey = row.GroupKey;
        settings.TouchpadManufacturer = device?.Manufacturer;
        settings.TouchpadProduct = device?.ProductName;
        settings.TouchpadPath = device?.Path;

        if (!settings.Save(out var saveError))
        {
            _sink?.Post($"[设置] ✗ 保存失败：{saveError}");
            StatusText.Text = $"已选定，但写入配置失败：{saveError}";
        }
        else
        {
            StatusText.Text = $"已记住「{row.Name}」，下次启动自动匹配。";
        }

        _onSelected?.Invoke();
    }
}