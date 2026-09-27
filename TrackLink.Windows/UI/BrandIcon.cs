using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace TrackLink.Windows.UI;

/// <summary>
/// 品牌图标（贴纸里那张根目录图）的两种取用方式：WPF 窗口要 <see cref="BitmapSource"/>，
/// WinForms 托盘要 <see cref="Icon"/>。两者都从同一个内嵌资源解码，不额外放 ico 文件。
/// </summary>
internal static class BrandIcon
{
    /// <summary>窗口图标用的位图（已冻结，可安全跨线程复用）。</summary>
    public static BitmapSource Source => _source ??= CreateSource();

    /// <summary>
    /// 托盘图标。<see cref="Bitmap.GetHicon"/> 得到的 HICON 必须自己 DestroyIcon，
    /// 所以这里先 <see cref="Icon.FromHandle"/> 再 <see cref="Icon.Clone"/> 拿一份独立的图标，最后释放句柄。
    /// </summary>
    public static Icon CreateIcon()
    {
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Source));
        encoder.Save(stream);
        stream.Position = 0;

        using var bitmap = new Bitmap(stream);
        var handle = bitmap.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static BitmapSource CreateSource()
    {
        var frame = Sticker.FirstFrame(StickerKey.Brand);
        if (frame is not null)
        {
            return frame;
        }

        // 贴纸资源缺失时退回系统图标，界面不会因为少一张图就打不开。
        return Imaging.CreateBitmapSourceFromHIcon(
            SystemIcons.Application.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    private static BitmapSource? _source;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}