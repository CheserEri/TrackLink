using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using TrackLink.Windows.UI;

namespace TrackLink.Windows;

/// <summary>
/// 入口。无参数 = 打开图形界面（默认用法）；带 <c>selftest</c> / <c>serve</c> / <c>remote</c> / <c>help</c> = 走控制台模式。
///
/// 工程输出类型是 <c>WinExe</c>（双击不弹黑窗口），代价是 <c>Console.Out</c> 默认指向 <c>TextWriter.Null</c>，
/// 因此控制台模式必须先挂上父进程的控制台并重建标准流，否则所有 <c>Console.WriteLine</c> 都被静默丢弃。
/// </summary>
internal static class Program
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [STAThread]
    private static int Main(string[] args)
    {
        var mode = args.FirstOrDefault(a => !a.StartsWith("--"))?.ToLowerInvariant();

        if (mode is null)
        {
            // 无参数：图形界面。App 里已挂 DispatcherUnhandledException 兜底。
            var app = new App();
            return app.Run();
        }

        PrepareConsole();
        return mode switch
        {
            "selftest" or "self" => ConsoleMode.RunSelfTest(),
            "serve" => ConsoleMode.RunServer(),
            "remote" => ConsoleMode.RunRemote(),
            _ => ConsoleMode.PrintUsage(),
        };
    }

    /// <summary>挂上父进程的控制台（从终端启动时）并在必要时新建控制台（双击时），随后重建标准输出流。</summary>
    private static void PrepareConsole()
    {
        if (!AttachConsole(AttachParentProcess))
        {
            AllocConsole();
        }

        // 必须开 AutoFlush，否则控制台看不到任何输出。
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });

        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 输出被重定向时可能不支持设置编码，忽略。
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();
}