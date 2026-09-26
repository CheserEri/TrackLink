using System.Collections.Specialized;
using System.Windows;

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

    public ConnectionView()
    {
        InitializeComponent();
    }

    public void Initialize(RemoteEngine engine, UiLogSink sink)
    {
        _engine = engine;
        _sink = sink;

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

        // 暂停态可能被全局热键改掉，这里同步勾选框（赋值前先摘掉事件，避免回写触发保存）。
        if (PauseCheck.IsChecked != engine.IsPaused)
        {
            PauseCheck.IsChecked = engine.IsPaused;
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