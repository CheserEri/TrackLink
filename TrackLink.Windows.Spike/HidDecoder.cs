using System.Runtime.InteropServices;
using System.Text;

namespace TrackLink.Windows.Spike;

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
    public byte IsAbsolute;
    public int DataIndex;
    public int DataIndexMin;
    public int DataIndexMax;
    public int LogicalMin;
    public int LogicalMax;
    public int PhysicalMin;
    public int PhysicalMax;
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

    public string UsageText => IsRange
        ? $"0x{UsagePage:X2}/0x{UsageMin:X2}-0x{UsageMax:X2}"
        : $"0x{UsagePage:X2}/0x{Usage:X2}";
}

/// <summary>
/// 单个触控板多触点 HID 集合的解析器：打印能力表，并用 HidP_GetData 按数据索引解码报文。
/// </summary>
internal sealed class TouchpadDecoder : IDisposable
{
    private IntPtr _handle = Win32Constants.InvalidHandleValue;
    private IntPtr _preparsed = IntPtr.Zero;
    private bool _diagnosed;

    public HIDP_CAPS Caps { get; private set; }
    public List<ValueField> Fields { get; } = new();
    public List<ButtonField> Buttons { get; } = new();
    public string? Error { get; private set; }
    public bool Ready => _preparsed != IntPtr.Zero;
    public int MaxContactsObserved { get; private set; }

    public TouchpadDecoder(string devicePath)
    {
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
        int status = NativeMethods.HidP_GetCaps(_preparsed, ref caps);
        if (status < 0)
        {
            Error = $"HidP_GetCaps 失败（0x{status:X8}）";
            return;
        }

        Caps = caps;
        ReadValueCaps();
        ReadButtonCaps();
    }

    private void ReadValueCaps()
    {
        int count = Caps.NumberInputValueCaps;
        if (count <= 0)
        {
            return;
        }

        var buffer = new HIDP_VALUE_CAPS[count];
        ushort length = (ushort)count;
        int status = NativeMethods.HidP_GetValueCaps(Win32Constants.HIDP_INPUT, buffer, ref length, _preparsed);
        if (status < 0)
        {
            Error = $"HidP_GetValueCaps 失败（0x{status:X8}）";
            return;
        }

        for (int i = 0; i < length; i++)
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
                IsAbsolute = c.IsAbsolute,
                DataIndex = c.U7,
                DataIndexMin = c.U7,
                DataIndexMax = c.U8,
                LogicalMin = c.LogicalMin,
                LogicalMax = c.LogicalMax,
                PhysicalMin = c.PhysicalMin,
                PhysicalMax = c.PhysicalMax,
                LinkCollection = c.LinkCollection,
            });
        }
    }

    /// <summary>HIDP_BUTTON_CAPS 为 72 字节固定布局，直接按偏移读取以避免结构体对齐风险。</summary>
    private void ReadButtonCaps()
    {
        int count = Caps.NumberInputButtonCaps;
        if (count <= 0)
        {
            return;
        }

        const int buttonCapsSize = 72;
        var raw = new byte[buttonCapsSize * count];
        ushort length = (ushort)count;
        int status = NativeMethods.HidP_GetButtonCaps(Win32Constants.HIDP_INPUT, raw, ref length, _preparsed);
        if (status < 0)
        {
            Error = $"HidP_GetButtonCaps 失败（0x{status:X8}）";
            return;
        }

        for (int i = 0; i < length; i++)
        {
            int o = i * buttonCapsSize;
            bool isRange = raw[o + 12] != 0;
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

    public void Describe(StringBuilder sb)
    {
        if (!Ready)
        {
            sb.AppendLine($"  解析器不可用：{Error}");
            return;
        }

        sb.AppendLine($"  顶层集合      : usagePage=0x{Caps.UsagePage:X2} usage=0x{Caps.Usage:X2}"
                      + $"  ({DescribeTopLevel(Caps.UsagePage, Caps.Usage)})");
        sb.AppendLine($"  输入报文长度  : {Caps.InputReportByteLength} 字节");
        sb.AppendLine($"  逻辑集合节点  : {Caps.NumberLinkCollectionNodes}"
                      + $"，输入按钮能力 {Caps.NumberInputButtonCaps}，输入值能力 {Caps.NumberInputValueCaps}"
                      + $"，输入数据索引数 {Caps.NumberInputDataIndices}");

        if (Fields.Count > 0)
        {
            sb.AppendLine("  值能力表（输入）:");
            foreach (var f in Fields)
            {
                sb.AppendLine($"    rid={f.ReportId} {f.UsageText,-14} 元素={f.ReportCount,-2} 位宽={f.BitSize,-3}"
                              + $" 逻辑={f.LogicalMin}..{f.LogicalMax} 物理={f.PhysicalMin}..{f.PhysicalMax}"
                              + $" 数据索引={(f.IsRange ? $"{f.DataIndexMin}..{f.DataIndexMax}" : $"{f.DataIndex}(x{f.ReportCount})"),-9}"
                              + $" 链表={f.LinkCollection}" + (f.IsAbsolute != 0 ? " 绝对" : " 相对"));
            }
        }

        if (Buttons.Count > 0)
        {
            sb.AppendLine("  按钮能力表（输入）:");
            foreach (var b in Buttons)
            {
                sb.AppendLine($"    rid={b.ReportId} {b.UsageText,-14} 数据索引={(b.IsRange ? $"{b.DataIndexMin}..{b.DataIndexMax}" : $"{b.DataIndex}"),-9}"
                              + $" 链表={b.LinkCollection}");
            }
        }

        sb.AppendLine($"  多触点字段    : ContactCount={(Has(0x0D, 0x54) ? "有" : "无")}"
                      + $" TipSwitch={(Has(0x0D, 0x42) ? "有" : "无")}"
                      + $" ContactId={(Has(0x0D, 0x51) ? "有" : "无")}"
                      + $" X={(Has(0x01, 0x30) ? "有" : "无")}"
                      + $" Y={(Has(0x01, 0x31) ? "有" : "无")}");
    }

    private bool Has(ushort page, ushort usage) =>
        Fields.Any(f => f.UsagePage == page && (f.IsRange ? f.UsageMin <= usage && usage <= f.UsageMax : f.Usage == usage))
        || Buttons.Any(b => b.UsagePage == page && (b.IsRange ? b.UsageMin <= usage && usage <= b.UsageMax : b.Usage == usage));

    public static string DescribeTopLevel(ushort page, ushort usage) => (page, usage) switch
    {
        (0x01, 0x02) => "鼠标集合",
        (0x01, 0x06) => "键盘集合",
        (0x0D, 0x05) => "触控板多触点集合",
        (0x0D, 0x04) => "触摸屏集合",
        _ => "其他集合",
    };

    public (string Line, string Signature) Decode(byte[] report, bool showHex)
    {
        if (!Ready)
        {
            return ($"rid={report[0]} len={report.Length} 无法解码（{Error}） hex={ToHex(report, showHex ? report.Length : 24)}",
                    $"raw={ToHex(report, 12)}");
        }

        var values = ReadData(report, out string diagnostic);

        var sb = new StringBuilder();
        sb.Append($"rid={report[0]} len={report.Length}");

        string cc = JoinValues(values, 0x0D, 0x54);
        string cid = JoinValues(values, 0x0D, 0x51);
        string tip = JoinValues(values, 0x0D, 0x42);
        string btn = JoinValues(values, 0x09, 0x01);
        string x = JoinValues(values, 0x01, 0x30);
        string y = JoinValues(values, 0x01, 0x31);

        sb.Append($" CC={cc} CID={cid} TIP={tip} BTN={btn} X={x} Y={y}");

        if (!_diagnosed)
        {
            _diagnosed = true;
            sb.Append($"\n{diagnostic}");
            sb.Append("   解码字段明细: ").Append(DescribeEntries(values));
            sb.Append('\n').Append("   报文 hex=").Append(ToHex(report, report.Length));
            sb.Append('\n').Append("   （以上为该集合的首条报文，供核对字段布局）");
        }
        else if (showHex)
        {
            sb.Append(" hex=").Append(ToHex(report, report.Length));
        }

        if (int.TryParse(cc, out int contacts))
        {
            MaxContactsObserved = Math.Max(MaxContactsObserved, contacts);
        }

        return (sb.ToString(), $"CC={cc} CID={cid} TIP={tip} BTN={btn}");
    }

    private static string JoinValues(Dictionary<(ushort, ushort), List<uint>> values, ushort page, ushort usage) =>
        values.TryGetValue((page, usage), out var list) && list.Count > 0
            ? string.Join("/", list)
            : "-";

    private string DescribeEntries(Dictionary<(ushort, ushort), List<uint>> values) =>
        string.Join(" ", values.Select(kv => $"[0x{kv.Key.Item1:X2}/0x{kv.Key.Item2:X2}]={string.Join("/", kv.Value)}"));

    /// <summary>
    /// 用 HidP_GetData 取出该报文全部数据项（数据索引 + 原始值），再按能力表映射回用途。
    /// 这比逐个 HidP_GetUsageValue 更可靠，可绕开链表/报文 ID 匹配问题。
    /// </summary>
    private Dictionary<(ushort Page, ushort Usage), List<uint>> ReadData(byte[] report, out string diagnostic)
    {
        var result = new Dictionary<(ushort, ushort), List<uint>>();
        var parts = new List<string>();

        int capacity = Math.Max(Caps.NumberInputDataIndices + 8, 16);
        var entries = new HIDP_DATA[capacity];
        uint length = (uint)capacity;

        IntPtr buffer = Marshal.AllocHGlobal(report.Length);
        try
        {
            Marshal.Copy(report, 0, buffer, report.Length);
            int status = NativeMethods.HidP_GetData(
                Win32Constants.HIDP_INPUT, entries, ref length, _preparsed, buffer, (uint)report.Length);

            if (status < 0)
            {
                diagnostic = $"   HidP_GetData 失败（0x{status:X8}）";
                return result;
            }

            var map = BuildDataIndexMap(report[0]);

            for (uint i = 0; i < length && i < entries.Length; i++)
            {
                int index = entries[i].DataIndex;
                if (!map.TryGetValue(index, out var usage))
                {
                    parts.Add($"idx{index}={entries[i].Value}?");
                    continue;
                }

                if (!result.TryGetValue(usage, out var list))
                {
                    list = new List<uint>();
                    result[usage] = list;
                }

                list.Add(entries[i].Value);
                parts.Add($"idx{index}=0x{usage.Item1:X2}/0x{usage.Item2:X2}={entries[i].Value}");
            }

            diagnostic = $"   HidP_GetData 成功，数据项={length}；GetUsageValue 状态："
                         + string.Join(" ", new (ushort Page, ushort Usage)[]
                         {
                             (0x0D, 0x54), (0x0D, 0x51), (0x0D, 0x42), (0x01, 0x30), (0x01, 0x31),
                         }.Select(u => $"0x{u.Page:X2}/0x{u.Usage:X2}=0x{ProbeUsage(report, buffer, u.Page, u.Usage):X8}"));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return result;
    }

    private int ProbeUsage(byte[] report, IntPtr buffer, ushort page, ushort usage)
    {
        int status = NativeMethods.HidP_GetUsageValue(
            Win32Constants.HIDP_INPUT, page, 0, usage, _preparsed, out _, buffer, (uint)report.Length);
        return status;
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

            int start = f.IsRange ? f.DataIndexMin : f.DataIndex;
            int count = f.IsRange ? Math.Max(1, f.DataIndexMax - f.DataIndexMin + 1) : Math.Max(1, f.ReportCount);
            for (int k = 0; k < count; k++)
            {
                ushort usage = f.IsRange ? (ushort)(f.UsageMin == f.UsageMax ? f.UsageMin : f.UsageMin) : f.Usage;
                map[start + k] = (f.UsagePage, usage);
            }
        }

        foreach (var b in Buttons)
        {
            if (b.ReportId != 0 && b.ReportId != reportId)
            {
                continue;
            }

            int start = b.IsRange ? b.DataIndexMin : b.DataIndex;
            int count = b.IsRange ? Math.Max(1, b.DataIndexMax - b.DataIndexMin + 1) : 1;
            for (int k = 0; k < count; k++)
            {
                map[start + k] = (b.UsagePage, b.IsRange ? (ushort)(b.UsageMin + k) : b.Usage);
            }
        }

        return map;
    }

    public static string ToHex(byte[] data, int maxBytes)
    {
        int n = Math.Min(data.Length, maxBytes);
        var sb = new StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }
            sb.Append(data[i].ToString("X2"));
        }
        if (n < data.Length)
        {
            sb.Append(" …");
        }
        return sb.ToString();
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