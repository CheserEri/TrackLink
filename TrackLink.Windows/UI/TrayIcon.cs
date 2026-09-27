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
    private readonly System.Drawing.Icon? _brand;

    public TrayIcon(Action onShow, Action onHide, Action onExit)
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主窗口", null, (_, _) => onShow());
        menu.Items.Add("最小化到托盘", null, (_, _) => onHide());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出 TrackLink", null, (_, _) => onExit());

        // 托盘图标与窗口图标同源（品牌贴纸）；解码失败时 BrandIcon 内部已回退到系统图标。
        _brand = BrandIcon.CreateIcon();

        _icon = new WinForms.NotifyIcon
        {
            Text = "TrackLink · 触控板遥控",
            Icon = _brand,
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

        // NotifyIcon 不接管外部传入的 Icon 生命周期，这里自己释放句柄，避免 GDI 对象泄漏。
        _brand?.Dispose();
    }
}