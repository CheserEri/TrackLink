using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace TrackLink.Windows.UI;

/// <summary>
/// 界面日志汇聚点。
///
/// 日志来自蓝牙接受线程、心跳线程、Raw Input 泵线程等多个后台线程，
/// 这里统一编组到 UI 线程再入缓冲，避免调用方各自处理线程问题。
/// 缓冲定长（500 行）：心跳每秒一条，长时间运行不封顶会吃光内存。
///
/// 刻意不使用 <c>SynchronizationContext.Current</c> 捕获：后台线程上创建对象时它可能是 null。
/// </summary>
internal sealed class UiLogSink(Dispatcher dispatcher)
{
    private const int MaxLines = 500;

    /// <summary>绑定到界面 ListBox 的行集合（仅 UI 线程访问）。</summary>
    public ObservableCollection<string> Lines { get; } = [];

    /// <summary>任一线程可调用：把一行日志编组到 UI 线程。</summary>
    public void Post(string line)
    {
        if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        var stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        if (dispatcher.CheckAccess())
        {
            Append(stamped);
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, () => Append(stamped));
    }

    /// <summary>清空日志面板。</summary>
    public void Clear()
    {
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(DispatcherPriority.Background, Clear);
            return;
        }

        Lines.Clear();
    }

    private void Append(string line)
    {
        Lines.Add(line);

        // 超出上限就从头丢，保证内存占用恒定。
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(0);
        }
    }
}