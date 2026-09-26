using WinForms = System.Windows.Forms;

namespace TrackLink.Windows.UI;

/// <summary>
/// 任务栏通知区域图标。
///
/// 用 WinForms 的 <see cref="WinForms.NotifyIcon"/> 而不是纯 P/Invoke <c>Shell_NotifyIcon</c>：
/// 后者要自己管 <c>NOTIFYICONDATA</c>、窗口消息与 <c>TaskbarCreated</c> 重建，代码量约三倍且无收益。
/// 必须在 UI 线程创建（内部隐藏窗口需要消息泵），退出前必须 <see cref="Dispose"/>，否则图标会残留在托盘。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;

    public TrayIcon(Action onShow, Action onHide, Action onExit)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => onShow());
        menu.Items.Add("最小化到托盘", null, (_, _) => onHide());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出 TrackLink", null, (_, _) => onExit());

        _icon = new WinForms.NotifyIcon
        {
            Text = "TrackLink · 触控板遥控",
            Icon = System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };

        _icon.DoubleClick += (_, _) => onShow();
    }

    public void Dispose()
    {
        // 先隐藏再释放，否则托盘里可能留下一个点不动的死图标。
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}