using System.Runtime.InteropServices;
using System.Text;

namespace TrackLink.Windows.Input;

/// <summary>一个 Raw Input 设备，以及它是否为本机触控板的多触点集合。</summary>
internal sealed class TouchpadDevice
{
    public IntPtr Handle;
    public string Path = "";
    public string ShortName = "";
    public string GroupKey = "";
    public uint Type;
    public uint VendorId;
    public uint ProductId;
    public ushort UsagePage;
    public ushort Usage;
    public bool IsTouchpadCollection;
    public TouchpadDecoder? Decoder;

    /// <summary>HID 描述符里的厂商名。取不到时为空串。</summary>
    public string Manufacturer = "";

    /// <summary>HID 描述符里的产品名。取不到时为空串。</summary>
    public string ProductName = "";

    /// <summary>界面上展示的名字：优先厂商名/产品名，取不到则退回 VID:PID，绝不猜测来源。</summary>
    public string DisplayName => Manufacturer.Length > 0 || ProductName.Length > 0
        ? $"{Manufacturer} {ProductName}".Trim()
        : $"VID:PID {VendorId:X4}:{ProductId:X4}";

    /// <summary>是否已把首条报文的诊断信息写进日志，避免重复刷屏。</summary>
    public string? FirstDump;
}

/// <summary>
/// 触控板枚举与识别。判定逻辑沿用第 1 周已验证的结论：
/// 凡顶层集合为 <c>0x0D/0x05</c> 的 HID 设备即触控板多触点集合。
/// </summary>
internal static class TouchpadCatalog
{
    /// <summary>枚举本机全部 Raw Input 设备，设备路径不可读的条目也会保留（路径显示为占位符）。</summary>
    public static List<TouchpadDevice> Enumerate()
    {
        var entries = new List<TouchpadDevice>();

        uint count = 0;
        var entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        if (NativeMethods.GetRawInputDeviceList(null, ref count, entrySize) == Win32Constants.RawInputDataError
            || count == 0)
        {
            return entries;
        }

        var list = new RAWINPUTDEVICELIST[count];
        var actual = NativeMethods.GetRawInputDeviceList(list, ref count, entrySize);
        if (actual == Win32Constants.RawInputDataError)
        {
            return entries;
        }

        for (uint i = 0; i < actual; i++)
        {
            entries.Add(BuildEntry(list[i].hDevice, list[i].dwType));
        }

        return entries;
    }

    public static TouchpadDevice BuildEntry(IntPtr hDevice, uint type)
    {
        var path = GetDeviceName(hDevice) ?? "(未知路径)";
        var (vendorId, productId, usagePage, usage) = GetDeviceInfo(hDevice);
        var (manufacturer, product) = GetHidStrings(path);

        return new TouchpadDevice
        {
            Handle = hDevice,
            Path = path,
            ShortName = ShortName(path),
            GroupKey = GroupKey(path),
            Type = type,
            VendorId = vendorId,
            ProductId = productId,
            UsagePage = usagePage,
            Usage = usage,
            Manufacturer = manufacturer,
            ProductName = product,
            IsTouchpadCollection = type == Win32Constants.RIM_TYPEHID
                                    && usagePage == Win32Constants.UsagePageDigitizer
                                    && usage == Win32Constants.UsageDigitizerTouchPad,
        };
    }

    public static string TypeText(uint type) =>
        type == Win32Constants.RIM_TYPEHID ? "HID" : $"T{type}";

    /// <summary>设备路径形如 <c>\\?\HID#ETD2303&amp;COL03#5&amp;1D09E9C1&amp;0&amp;0002#{GUID}</c>。</summary>
    public static string ShortName(string path)
    {
        var segments = path.Split('#');
        return segments.Length >= 2 ? segments[1] : path;
    }

    /// <summary>去掉实例序列号，得到「物理设备」级分组键，用于把 COL01/COL03 归为同一块触控板。</summary>
    public static string GroupKey(string path)
    {
        var segments = path.Split('#');
        if (segments.Length < 3)
        {
            return path;
        }

        var instance = segments[2];
        var cut = instance.LastIndexOf('&');
        return cut > 0 ? instance[..cut] : instance;
    }

    private static string? GetDeviceName(IntPtr hDevice)
    {
        uint size = 0;
        NativeMethods.GetRawInputDeviceInfo(hDevice, Win32Constants.RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)size * sizeof(char));
        try
        {
            var result = NativeMethods.GetRawInputDeviceInfo(hDevice, Win32Constants.RIDI_DEVICENAME, buffer, ref size);
            return result == Win32Constants.RawInputDataError ? null : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 读 HID 描述符里的厂商名 / 产品名（计划书要求设备选择时展示厂商名）。
    /// 触控板是独占用例设备，打开时不能带共享标志；取不到就返回空串，由界面回退显示 VID:PID。
    /// </summary>
    private static (string Manufacturer, string Product) GetHidStrings(string path)
    {
        if (path == "(未知路径)")
        {
            return ("", "");
        }

        // GENERIC_READ / FILE_SHARE_READ|WRITE / OPEN_EXISTING
        var handle = CreateFileW(path, 0x80000000, 0x00000003, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == new IntPtr(-1))
        {
            return ("", "");
        }

        try
        {
            return (ReadHidString(handle, HidD_GetManufacturerString),
                    ReadHidString(handle, HidD_GetProductString));
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static string ReadHidString(IntPtr handle, Func<IntPtr, byte[], uint, bool> getter)
    {
        var buffer = new byte[512];
        if (!getter(handle, buffer, (uint)buffer.Length))
        {
            return "";
        }

        var text = Encoding.Unicode.GetString(buffer);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }

    private static (uint VendorId, uint ProductId, ushort UsagePage, ushort Usage) GetDeviceInfo(IntPtr hDevice)
    {
        const int infoSize = 32;
        var buffer = Marshal.AllocHGlobal(infoSize);
        try
        {
            for (var i = 0; i < infoSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            Marshal.WriteInt32(buffer, 0, infoSize);
            uint size = infoSize;
            if (NativeMethods.GetRawInputDeviceInfo(hDevice, Win32Constants.RIDI_DEVICEINFO, buffer, ref size)
                == Win32Constants.RawInputDataError)
            {
                return (0, 0, 0, 0);
            }

            var bytes = new byte[infoSize];
            Marshal.Copy(buffer, bytes, 0, infoSize);

            // RID_DEVICE_INFO：cbSize(4) dwType(4) union(24)；HID 分支为 VID/PID/Version/UsagePage/Usage。
            return (BitConverter.ToUInt32(bytes, 8), BitConverter.ToUInt32(bytes, 12),
                    BitConverter.ToUInt16(bytes, 20), BitConverter.ToUInt16(bytes, 22));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(IntPtr device, byte[] buffer, uint bufferLength);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr device, byte[] buffer, uint bufferLength);
}