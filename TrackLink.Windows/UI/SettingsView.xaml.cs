using System.Windows;
using TrackLink.Windows.Settings;

namespace TrackLink.Windows.UI;

/// <summary>
/// 手势设置页（计划书第 111-118 行）：灵敏度、边缘加速、反向滚动、安全停止热键。
///
/// 手势线程每次判定都现读同一份 <see cref="GestureOptions"/>，所以这里的改动**立即生效**，
/// 不需要重启监听，也不需要重建识别器；顺手把整份配置写回 settings.json。
/// </summary>
internal partial class SettingsView : System.Windows.Controls.UserControl
{
    private AppSettings? _settings;
    private UiLogSink? _sink;

    /// <summary>初始化期间赋控件初值会触发 ValueChanged，用这个标志挡住，避免把默认值写回配置。</summary>
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>停止热键改动时触发，由 MainWindow 重新注册全局热键。</summary>
    public event Action? StopHotkeyChanged;

    public void Initialize(AppSettings settings, UiLogSink sink)
    {
        _settings = settings;
        _sink = sink;

        _loading = true;
        MoveGainSlider.Value = settings.Gesture.MoveGain;
        EdgeCheck.IsChecked = settings.Gesture.EdgeAccelEnabled;
        EdgeFactorSlider.Value = settings.Gesture.EdgeAccelFactor;
        ReverseScrollCheck.IsChecked = settings.Gesture.ReverseScroll;

        HotkeyCombo.ItemsSource = GestureOptions.HotkeyNames;
        HotkeyCombo.SelectedItem = GestureOptions.HotkeyNames.Contains(settings.Gesture.StopHotkey)
            ? settings.Gesture.StopHotkey
            : GestureOptions.DefaultStopHotkey;

        _loading = false;
        UpdateTexts();
        ConfigPathText.Text = $"配置文件：{AppSettings.Location}";
    }

    /// <summary>由 MainWindow 填入热键注册结果（注册失败不静默，直接显示在界面上）。</summary>
    public void SetHotkeyStatus(string text) => HotkeyStatusText.Text = text;

    private void OnMoveGainChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Apply("光标灵敏度");

    private void OnEdgeFactorChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        Apply("边缘加速系数");

    private void OnEdgeChanged(object sender, RoutedEventArgs e) => Apply("边缘加速");

    private void OnReverseScrollChanged(object sender, RoutedEventArgs e) => Apply("反向滚动");

    private void OnHotkeyChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        Apply("安全停止热键");
        StopHotkeyChanged?.Invoke();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        if (_settings is null)
        {
            return;
        }

        _settings.Gesture.Reset();

        _loading = true;
        MoveGainSlider.Value = _settings.Gesture.MoveGain;
        EdgeCheck.IsChecked = _settings.Gesture.EdgeAccelEnabled;
        EdgeFactorSlider.Value = _settings.Gesture.EdgeAccelFactor;
        ReverseScrollCheck.IsChecked = _settings.Gesture.ReverseScroll;
        HotkeyCombo.SelectedItem = _settings.Gesture.StopHotkey;
        _loading = false;

        Apply("已恢复默认值");
        StopHotkeyChanged?.Invoke();
    }

    private void Apply(string what)
    {
        // XAML 加载阶段给 Minimum/Maximum 赋值会强制修正 Value，从而触发 ValueChanged，
        // 此时控件字段（MoveGainText 等）还没被赋值，也还没有配置对象——直接忽略。
        if (_settings is null)
        {
            return;
        }

        if (_loading)
        {
            UpdateTexts();
            return;
        }

        var gesture = _settings.Gesture;
        gesture.MoveGain = MoveGainSlider.Value;
        gesture.EdgeAccelEnabled = EdgeCheck.IsChecked == true;
        gesture.EdgeAccelFactor = EdgeFactorSlider.Value;
        gesture.ReverseScroll = ReverseScrollCheck.IsChecked == true;

        if (HotkeyCombo.SelectedItem is string hotkey)
        {
            gesture.StopHotkey = hotkey;
        }

        UpdateTexts();

        if (_settings.Save(out var error))
        {
            SaveStatusText.Text = $"{what}已保存，立即生效。";
        }
        else
        {
            SaveStatusText.Text = $"✗ 写入配置失败：{error}";
            _sink?.Post($"[设置] ✗ 写入配置失败：{error}");
        }
    }

    private void UpdateTexts()
    {
        MoveGainText.Text = $"{MoveGainSlider.Value:0.0}×";
        EdgeFactorText.Text = $"{EdgeFactorSlider.Value:0.0}×";
        EdgeFactorSlider.IsEnabled = EdgeCheck.IsChecked == true;
    }
}