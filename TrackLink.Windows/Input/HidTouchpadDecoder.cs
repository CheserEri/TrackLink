using System.Runtime.InteropServices;
using System.Text;

namespace TrackLink.Windows.Input;

/// <summary>一条 HID 报文里的单个接触点。</summary>
internal readonly record struct ContactPoint(int Id, int Tip, int X, int Y);

/// <summary>
/// 一条 HID 报文的解码结果。
///
/// 注意：一条报文通常只携带一个接触点的数据，且 <see cref="ContactCount"/> 只在每帧第一条报文里非 0，
/// 因此不能把单条报文当成一次完整手势——必须按 ContactID 累积（见 <see cref="GestureRecognizer"/>）。
/// </summary>
internal sealed class TouchContactReport
{
    public int ContactCount { get; init; }

    /// <summary>物理按键（0x09/0x01）是否置位。</summary>
    public bool Button { get; init; }

    public ContactPoint[] Contacts { get; init; } = [];
}

internal sealed class ValueField
{
    public ushort UsagePage;
    public ushort Usage;
    public ushort UsageMin;
    public ushort UsageMax;
    public byte ReportId;
    public bool IsRange;
    public int ReportCount;
    public int BitSize;
    public int DataIndex;
    public int DataIndexMin;
    public int DataIndexMax;
    public int LogicalMin;
    public int LogicalMax;
    public ushort LinkCollection;

    public string UsageText => IsRange
        ? $"0x{UsagePage:X2}/0x{UsageMin:X2}-0x{UsageMax:X2}"
        : $"0x{UsagePage:X2}/0x{Usage:X2}";
}

internal sealed class ButtonField
{
    public ushort UsagePage;
    public ushort Usage;
    public ushort UsageMin;
    public ushort UsageMax;
    public byte ReportId;
    public bool IsRange;
    public ushort LinkCollection;
    public int DataIndex;
    public int DataIndexMin;
    public int DataIndexMax;
}

/// <summary>
/// 单个触控板多触点 HID 集合的解析器。
///
/// 第 1 周已确认：本机 ELAN 触控板上 `HidP_GetUsageValue` 对全部用途都失败（NTSTATUS 0xC0110001），
/// 必须走 `HidP_GetData` + 能力表数据索引映射。这条解析路径不要改动。
/// </summary>
internal sealed class TouchpadDecoder : IDisposable
{
    /// <summary>第 1 周实测的逻辑范围，能力表读数不可用时作为回退。</summary>
    public const int FallbackXMin = 0;
    public const int FallbackXMax = 3864;
    public const int FallbackYMin = 0;
    public const int FallbackYMax = 2499;

    private IntPtr _handle = Win32Constants.InvalidHandleValue;
    private IntPtr _preparsed = IntPtr.Zero;
    private bool _diagnosed;

    public HIDP_CAPS Caps { get; private set; }
    public List<ValueField> Fields { get; } = [];
    public List<ButtonField> Buttons { get; } = [];
    public string? Error { get; private set; }
    public bool Ready => _preparsed != IntPtr.Zero;

    /// <summary>首条成功解码报文的能力表/字段明细，供启动阶段核对布局。</summary>
    public string? FirstReportDump { get; private set; }

    public int MaxContactsObserved { get; private set; }

    /// <summary>X 轴的归一化基准来源是否为回退值（用于启动日志提示）。</summary>
    public bool UsingFallbackRanges { get; private set; }

    public (int Min, int Max) XRange { get; private set; } = (FallbackXMin, FallbackXMax);
    public (int Min, int Max) YRange { get; private set; } = (FallbackYMin, FallbackYMax);

    public string DevicePath { get; }

    public TouchpadDecoder(string devicePath)
    {
        DevicePath = devicePath;

        _handle = NativeMethods.CreateFile(
            devicePath, Win32Constants.GENERIC_READ,
            Win32Constants.FILE_SHARE_READ | Win32Constants.FILE_SHARE_WRITE,
            IntPtr.Zero, Win32Constants.OPEN_EXISTING, 0, IntPtr.Zero);

        if (_handle == Win32Constants.InvalidHandleValue)
        {
            _handle = NativeMethods.CreateFile(
                devicePath, 0,
                Win32Constants.FILE_SHARE_READ | Win32Constants.FILE_SHARE_WRITE,
                IntPtr.Zero, Win32Constants.OPEN_EXISTING, 0, IntPtr.Zero);
        }

        if (_handle == Win32Constants.InvalidHandleValue)
        {
            Error = $"CreateFile 失败（Win32 {Marshal.GetLastWin32Error()}）";
            return;
        }

        if (!NativeMethods.HidD_GetPreparsedData(_handle, out _preparsed) || _preparsed == IntPtr.Zero)
        {
            Error = $"HidD_GetPreparsedData 失败（Win32 {Marshal.GetLastWin32Error()}）";
            return;
        }

        var caps = new HIDP_CAPS();
        if (NativeMethods.HidP_GetCaps(_preparsed, ref caps) < 0)
        {
            Error = "HidP_GetCaps 失败";
            return;
        }

        Caps = caps;
        ReadValueCaps();
        ReadButtonCaps();
        ResolveCoordinateRanges();
    }

    private void ReadValueCaps()
    {
        var count = Caps.NumberInputValueCaps;
        if (count <= 0)
        {
            return;
        }

        var buffer = new HIDP_VALUE_CAPS[count];
        ushort length = count;
        if (NativeMethods.HidP_GetValueCaps(Win32Constants.HIDP_INPUT, buffer, ref length, _preparsed) < 0)
        {
            Error = "HidP_GetValueCaps 失败";
            return;
        }

        for (var i = 0; i < length; i++)
        {
            var c = buffer[i];
            Fields.Add(new ValueField
            {
                UsagePage = c.UsagePage,
                Usage = c.U1,
                UsageMin = c.U1,
                UsageMax = c.U2,
                ReportId = c.ReportID,
                IsRange = c.IsRange != 0,
                ReportCount = c.ReportCount,
                BitSize = c.BitSize,
                DataIndex = c.U7,
                DataIndexMin = c.U7,
                DataIndexMax = c.U8,
                LogicalMin = c.LogicalMin,
                LogicalMax = c.LogicalMax,
                LinkCollection = c.LinkCollection,
            });
        }
    }

    /// <summary>HIDP_BUTTON_CAPS 为 72 字节固定布局，直接按偏移读取以避免结构体对齐风险。</summary>
    private void ReadButtonCaps()
    {
        var count = Caps.NumberInputButtonCaps;
        if (count <= 0)
        {
            return;
        }

        const int buttonCapsSize = 72;
        var raw = new byte[buttonCapsSize * count];
        ushort length = count;
        if (NativeMethods.HidP_GetButtonCaps(Win32Constants.HIDP_INPUT, raw, ref length, _preparsed) < 0)
        {
            Error = "HidP_GetButtonCaps 失败";
            return;
        }

        for (var i = 0; i < length; i++)
        {
            var o = i * buttonCapsSize;
            var isRange = raw[o + 12] != 0;
            Buttons.Add(new ButtonField
            {
                UsagePage = BitConverter.ToUInt16(raw, o + 0),
                ReportId = raw[o + 2],
                LinkCollection = BitConverter.ToUInt16(raw, o + 6),
                IsRange = isRange,
                Usage = BitConverter.ToUInt16(raw, o + 56),
                UsageMin = BitConverter.ToUInt16(raw, o + 56),
                UsageMax = BitConverter.ToUInt16(raw, o + 58),
                DataIndex = BitConverter.ToUInt16(raw, o + 68),
                DataIndexMin = BitConverter.ToUInt16(raw, o + 68),
                DataIndexMax = BitConverter.ToUInt16(raw, o + 70),
            });
        }
    }

    /// <summary>取 X/Y 的逻辑范围做坐标归一化；能力表读数不可用时回退到第 1 周实测范围。</summary>
    private void ResolveCoordinateRanges()
    {
        var x = FindField(Win32Constants.UsagePageGenericDesktop, Win32Constants.UsageGenericX);
        var y = FindField(Win32Constants.UsagePageGenericDesktop, Win32Constants.UsageGenericY);

        var xOk = x is not null && x.LogicalMax > x.LogicalMin;
        var yOk = y is not null && y.LogicalMax > y.LogicalMin;

        if (xOk)
        {
            XRange = (x!.LogicalMin, x.LogicalMax);
        }

        if (yOk)
        {
            YRange = (y!.LogicalMin, y.LogicalMax);
        }

        UsingFallbackRanges = !xOk || !yOk;
    }

    private ValueField? FindField(ushort page, ushort usage) =>
        Fields.FirstOrDefault(f => f.UsagePage == page
                                   && (f.IsRange ? f.UsageMin <= usage && usage <= f.UsageMax : f.Usage == usage));

    /// <summary>把一条 HID 报文解成结构化接触点数据。返回 false 表示解析器不可用。</summary>
    public bool TryDecode(byte[] report, out TouchContactReport result)
    {
        result = new TouchContactReport();

        if (!Ready)
        {
            return false;
        }

        var values = ReadData(report, out _);
        if (!_diagnosed)
        {
            _diagnosed = true;
            FirstReportDump = BuildDiagnostic(report, values);
        }

        var ids = GetList(values, Win32Constants.UsagePageDigitizer, Win32Constants.UsageDigitizerContactId);
        var tips = GetList(values, Win32Constants.UsagePageDigitizer, Win32Constants.UsageDigitizerTipSwitch);
        var xs = GetList(values, Win32Constants.UsagePageGenericDesktop, Win32Constants.UsageGenericX);
        var ys = GetList(values, Win32Constants.UsagePageGenericDesktop, Win32Constants.UsageGenericY);
        var counts = GetList(values, Win32Constants.UsagePageDigitizer, Win32Constants.UsageDigitizerContactCount);
        var buttons = GetList(values, 0x09, Win32Constants.UsageButtonPrimary);

        var contacts = new ContactPoint[Math.Max(xs.Count, Math.Max(ids.Count, ys.Count))];
        for (var i = 0; i < contacts.Length; i++)
        {
            contacts[i] = new ContactPoint(
                Id: (int)At(ids, i, (uint)i),
                Tip: (int)At(tips, i, 0),
                X: (int)At(xs, i, 0),
                Y: (int)At(ys, i, 0));
        }

        var contactCount = counts.Count > 0 ? (int)counts[0] : 0;
        MaxContactsObserved = Math.Max(MaxContactsObserved, Math.Max(contactCount, contacts.Length));

        result = new TouchContactReport
        {
            ContactCount = contactCount,
            Button = buttons.Count > 0 && buttons[0] != 0,
            Contacts = contacts,
        };
        return true;
    }

    /// <summary>
    /// 列表比索引短时：只有唯一一个元素说明该字段是「单接触点」写法，沿用第 0 项；
    /// 其余情况取默认值（TipSwitch 取 0，即未按下）。
    /// </summary>
    private static uint At(List<uint> list, int index, uint fallback)
    {
        if (list.Count == 0)
        {
            return fallback;
        }

        if (index < list.Count)
        {
            return list[index];
        }

        return list.Count == 1 ? list[0] : fallback;
    }

    private static List<uint> GetList(
        Dictionary<(ushort Page, ushort Usage), List<uint>> values,
        ushort page,
        ushort usage) =>
        values.TryGetValue((page, usage), out var list) ? list : [];

    private string BuildDiagnostic(
        byte[] report,
        Dictionary<(ushort Page, ushort Usage), List<uint>> values)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"   顶层集合 0x{Caps.UsagePage:X2}/0x{Caps.Usage:X2}，输入报文 {Caps.InputReportByteLength} 字节");
        sb.AppendLine($"   值能力表 {Fields.Count} 项：");
        foreach (var f in Fields)
        {
            sb.AppendLine($"     rid={f.ReportId} {f.UsageText,-14} 元素={f.ReportCount,-2}"
                          + $" 逻辑={f.LogicalMin}..{f.LogicalMax} 索引={f.DataIndex}");
        }

        sb.AppendLine($"   按钮能力表 {Buttons.Count} 项：");
        foreach (var b in Buttons)
        {
            sb.AppendLine($"     rid={b.ReportId} 0x{b.UsagePage:X2}/0x{b.Usage:X2} 索引={b.DataIndex}");
        }

        sb.AppendLine("   首条报文解码字段："
                      + string.Join(" ", values.Select(kv =>
                          $"[0x{kv.Key.Page:X2}/0x{kv.Key.Usage:X2}]={string.Join("/", kv.Value)}")));
        sb.AppendLine($"   首条报文 hex={ToHex(report, report.Length)}");
        return sb.ToString();
    }

    /// <summary>
    /// 用 HidP_GetData 取出该报文全部数据项（数据索引 + 原始值），再按能力表映射回用途。
    /// 这比逐个 HidP_GetUsageValue 更可靠，可绕开链表/报文 ID 匹配问题。
    /// </summary>
    private Dictionary<(ushort Page, ushort Usage), List<uint>> ReadData(byte[] report, out string diagnostic)
    {
        var result = new Dictionary<(ushort, ushort), List<uint>>();

        var capacity = Math.Max(Caps.NumberInputDataIndices + 8, 16);
        var entries = new HIDP_DATA[capacity];
        uint length = (uint)capacity;

        var buffer = Marshal.AllocHGlobal(report.Length);
        try
        {
            Marshal.Copy(report, 0, buffer, report.Length);
            var status = NativeMethods.HidP_GetData(
                Win32Constants.HIDP_INPUT, entries, ref length, _preparsed, buffer, (uint)report.Length);

            if (status < 0)
            {
                diagnostic = $"   HidP_GetData 失败（0x{status:X8}）";
                return result;
            }

            var map = BuildDataIndexMap(report[0]);

            for (uint i = 0; i < length && i < entries.Length; i++)
            {
                if (!map.TryGetValue(entries[i].DataIndex, out var usage))
                {
                    continue;
                }

                if (!result.TryGetValue(usage, out var list))
                {
                    list = [];
                    result[usage] = list;
                }

                list.Add(entries[i].Value);
            }

            diagnostic = $"   HidP_GetData 成功，数据项={length}";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private Dictionary<int, (ushort Page, ushort Usage)> BuildDataIndexMap(byte reportId)
    {
        var map = new Dictionary<int, (ushort, ushort)>();

        foreach (var f in Fields)
        {
            if (f.ReportId != 0 && f.ReportId != reportId)
            {
                continue;
            }

            var start = f.IsRange ? f.DataIndexMin : f.DataIndex;
            var count = f.IsRange ? Math.Max(1, f.DataIndexMax - f.DataIndexMin + 1) : Math.Max(1, f.ReportCount);
            for (var k = 0; k < count; k++)
            {
                map[start + k] = (f.UsagePage, f.Usage);
            }
        }

        foreach (var b in Buttons)
        {
            if (b.ReportId != 0 && b.ReportId != reportId)
            {
                continue;
            }

            var start = b.IsRange ? b.DataIndexMin : b.DataIndex;
            var count = b.IsRange ? Math.Max(1, b.DataIndexMax - b.DataIndexMin + 1) : 1;
            for (var k = 0; k < count; k++)
            {
                map[start + k] = (b.UsagePage, b.IsRange ? (ushort)(b.UsageMin + k) : b.Usage);
            }
        }

        return map;
    }

    public static string DescribeTopLevel(ushort page, ushort usage) => (page, usage) switch
    {
        (0x01, 0x02) => "鼠标集合",
        (0x01, 0x06) => "键盘集合",
        (0x0D, 0x05) => "触控板多触点集合",
        (0x0D, 0x04) => "触摸屏集合",
        _ => "其他集合",
    };

    public static string ToHex(byte[] data, int maxBytes)
    {
        var n = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(n * 3);
        for (var i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(data[i].ToString("X2"));
        }

        return n < data.Length ? sb.Append(" …").ToString() : sb.ToString();
    }

    public void Dispose()
    {
        if (_preparsed != IntPtr.Zero)
        {
            NativeMethods.HidD_FreePreparsedData(_preparsed);
            _preparsed = IntPtr.Zero;
        }

        if (_handle != Win32Constants.InvalidHandleValue)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = Win32Constants.InvalidHandleValue;
        }
    }
}