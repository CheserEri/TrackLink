using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using TrackLink.Windows.Input;

namespace TrackLink.Windows;

/// <summary>
/// 图形界面应用主体。刻意不建 <c>App.xaml</c>：以 <c>App.xaml</c> 命名的文件会被 XAML 编译器当作
/// <c>ApplicationDefinition</c> 并自动生成带 <c>[STAThread] Main</c> 的入口，与 <see cref="Program"/> 的 Main 冲突（CS0017）。
/// </summary>
internal sealed class App : System.Windows.Application
{
    /// <summary>主窗口标题，同时作为单实例唤醒时的查找依据。</summary>
    public const string WindowTitle = "TrackLink · 触控板遥控";

    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 原神风格主题字典必须先于任何窗口加载，否则控件会先用系统默认样式完成一次布局再被重绘。
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/UI/Theme/GenshinTheme.xaml", UriKind.Absolute),
        });

        // 兜底摘钩子：本机鼠标闸门是低层钩子，进程退出后系统会摘，但主动摘更干净。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LocalMouseBlocker.Stop();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => LocalMouseBlocker.Stop();

        // 单实例是必需的：两个实例各自 bind(BT_PORT_ANY) 会拿到不同 RFCOMM 通道，
        // 却注册同一个 UUID 的 SDP 记录，手机可能连到错误的通道。
        _instanceMutex = new Mutex(true, @"Local\TrackLink.Windows.SingleInstance", out var created);
        if (!created)
        {
            ActivateExistingInstance();
            Shutdown(0);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LocalMouseBlocker.Stop();

        // 显示完整异常链（含 InnerException）：WPF 抛出的 TargetInvocationException 会把真正的
        // 原因藏在内部异常里，只显示 Message 会排查不到根因。
        System.Windows.MessageBox.Show(
            $"TrackLink 遇到未处理的错误，即将退出：\n\n{e.Exception}",
            WindowTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        e.Handled = true;
        Shutdown(-1);
    }

    /// <summary>唤起已在运行的实例：把它的窗口显示出来并置前（可能只是被最小化到托盘了）。</summary>
    private static void ActivateExistingInstance()
    {
        var window = FindWindow(null, WindowTitle);
        if (window == IntPtr.Zero)
        {
            System.Windows.MessageBox.Show("TrackLink 已经在运行（请查看任务栏通知区域的图标）。",
                WindowTitle, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowWindow(window, SwRestore);
        ShowWindow(window, SwShow);
        SetForegroundWindow(window);
    }

    private const int SwShow = 5;
    private const int SwRestore = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
}