using System.Runtime.InteropServices;

namespace TrackLink.Windows.Bluetooth;

/// <summary>Winsock 蓝牙（AF_BTH）相关常量与结构体。</summary>
internal static class Bt
{
    // 地址族与协议
    public const int AF_BTH = 32;
    public const int BTHPROTO_RFCOMM = 3;
    public const int SOCK_STREAM = 1;

    /// <summary>BT_PORT_ANY：由协议栈自动分配 RFCOMM 通道号。</summary>
    public const uint BT_PORT_ANY = 0xFFFFFFFF;

    // 套接字选项
    public const int SOL_SOCKET = 0xFFFF;
    public const int SO_RCVTIMEO = 0x1006;

    // WSASetService / WSALookupService
    public const uint NS_BTH = 16;
    public const uint RNRSERVICE_REGISTER = 0;

    /// <summary>停止播发服务。注意 Bluetooth 命名空间下 <c>RNRSERVICE_DEREGISTER</c> 无效，删除必须用本值。</summary>
    public const uint RNRSERVICE_DELETE = 2;
    public const uint LUP_RETURN_ADDR = 0x00000100;
    public const uint LUP_RETURN_NAME = 0x00000010;

    /// <summary>BTH_SET_SERVICE.pSdpVersion 的入参值，由 bthdef.h 定义。</summary>
    public const uint BTH_SDP_VERSION = 1;

    public static readonly IntPtr InvalidSocket = new(-1);

    public static string FormatAddress(ulong btAddr) =>
        string.Join(":", Enumerable.Range(0, 6).Select(i => ((btAddr >> (8 * (5 - i))) & 0xFF).ToString("X2")));

    public static int LastError => Marshal.GetLastWin32Error();
}

/// <summary>
/// 蓝牙套接字地址。必须 <c>Pack = 1</c>（30 字节）：ws2bth.h 用 pshpack1.h 包裹该结构，
/// 若按 C# 默认的 8 字节对齐会得到 40 字节并让 btAddr/port 偏移整体错位，
/// 表现为 bind 恒定返回 WSAEADDRNOTAVAIL(10049)。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct SOCKADDR_BTH
{
    public ushort addressFamily;
    public ulong btAddr;
    public Guid serviceClassId;
    public uint port;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SOCKET_ADDRESS
{
    public IntPtr lpSockaddr;
    public int iSockaddrLength;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CSADDR_INFO
{
    public SOCKET_ADDRESS LocalAddr;
    public SOCKET_ADDRESS RemoteAddr;
    public int iSocketType;
    public int iProtocol;
}

[StructLayout(LayoutKind.Sequential)]
internal struct BLOB
{
    public uint cbSize;
    public IntPtr pBlobData;
}

/// <summary>
/// 注册 SDP 记录时必须由 <see cref="BLOB.pBlobData"/> 指向本结构（而非裸 SDP 记录）。
/// 字段布局与 ws2bth.h 一致：<c>pRecord</c> 的偏移为 44，
/// 若直接把 SDP 字节当作本结构传入，Windows 会把记录开头的 4 字节当成 <c>ulRecordLength</c>
/// 并据此越界读取 <c>pRecord</c>，表现为 <c>WSASetService</c> 抛 AccessViolationException。
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BTH_SET_SERVICE
{
    /// <summary>入参须指向值为 <see cref="Bt.BTH_SDP_VERSION"/> 的 ULONG。</summary>
    public IntPtr pSdpVersion;

    /// <summary>入参须指向一个取值为 0 的 HANDLE；注册成功后系统把记录句柄写入该地址，删除记录时须原样传回。</summary>
    public IntPtr pRecordHandle;

    public uint fCodService;

    public uint reserved0;
    public uint reserved1;
    public uint reserved2;
    public uint reserved3;
    public uint reserved4;

    /// <summary>SDP 记录的字节数。</summary>
    public uint ulRecordLength;

    /// <summary>记录首字节；实际记录长度由 <see cref="ulRecordLength"/> 决定，故整块按 44 + 记录长度分配。</summary>
    public byte pRecord;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct WSAQUERYSETW
{
    public uint dwSize;
    public string? lpszServiceInstanceName;
    public IntPtr lpServiceClassId;
    public IntPtr lpVersion;
    public string? lpszComment;
    public uint dwNameSpace;
    public IntPtr lpNSProviderId;
    public string? lpszContext;
    public uint dwNumberOfProtocols;
    public IntPtr lpafpProtocols;
    public string? lpszQueryString;
    public uint dwNumberOfCsAddrs;
    public IntPtr lpcsaddrBuffer;
    public uint dwOutputFlags;
    public IntPtr lpBlob;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct WSADATA
{
    public ushort wVersion;
    public ushort wHighVersion;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)]
    public string szDescription;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 129)]
    public string szSystemStatus;
    public ushort iMaxSockets;
    public ushort iMaxUdpDg;
    public IntPtr lpVendorInfo;
}

internal static class Winsock
{
    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSAStartup(ushort wVersionRequested, out WSADATA lpWSAData);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSACleanup();

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern IntPtr socket(int af, int type, int protocol);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int bind(IntPtr s, IntPtr name, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int connect(IntPtr s, IntPtr name, int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int listen(IntPtr s, int backlog);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern IntPtr accept(IntPtr s, IntPtr addr, IntPtr addrlen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int getsockname(IntPtr s, IntPtr name, ref int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    private static extern int getpeername(IntPtr s, IntPtr name, ref int namelen);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int send(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int recv(IntPtr s, byte[] buf, int len, int flags);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int closesocket(IntPtr s);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int setsockopt(IntPtr s, int level, int optname, ref int optval, int optlen);

    /// <summary>
    /// 注册 / 删除 SDP 服务记录。入参为裸指针：调用方须完全手工铺 <c>WSAQUERYSETW</c>，
    /// 避免 marshaler 在 In/Out 往返时对 <c>string</c> 字段做二次分配带来的不确定性。
    /// </summary>
    [DllImport("ws2_32.dll", SetLastError = true, EntryPoint = "WSASetServiceW")]
    public static extern int WSASetService(IntPtr lpqsRegInfo, uint essoperation, uint dwControlFlags);

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode, EntryPoint = "WSALookupServiceBeginW", SetLastError = true)]
    public static extern int WSALookupServiceBegin(ref WSAQUERYSETW lpqsRestrictions, uint dwControlFlags, out IntPtr lphLookup);

    [DllImport("ws2_32.dll", CharSet = CharSet.Unicode, EntryPoint = "WSALookupServiceNextW", SetLastError = true)]
    public static extern int WSALookupServiceNext(IntPtr hLookup, uint dwControlFlags, ref int lpdwBufferLength, IntPtr lpqsResults);

    [DllImport("ws2_32.dll", SetLastError = true)]
    public static extern int WSALookupServiceEnd(IntPtr hLookup);

    // ---- 带 SOCKADDR_BTH 的便捷封装（结构体只在托管侧构造，指针临时分配） ----

    public static int Bind(IntPtr socketHandle, SOCKADDR_BTH address) =>
        WithSockaddr(address, ptr => bind(socketHandle, ptr, Marshal.SizeOf<SOCKADDR_BTH>()));

    public static int Connect(IntPtr socketHandle, SOCKADDR_BTH address) =>
        WithSockaddr(address, ptr => connect(socketHandle, ptr, Marshal.SizeOf<SOCKADDR_BTH>()));

    public static SOCKADDR_BTH? GetLocalAddress(IntPtr socketHandle) => ReadSockaddr(socketHandle, peer: false);

    public static SOCKADDR_BTH? GetPeerAddress(IntPtr socketHandle) => ReadSockaddr(socketHandle, peer: true);

    public static bool SetRecvTimeout(IntPtr socketHandle, int milliseconds)
    {
        var value = milliseconds;
        return setsockopt(socketHandle, Bt.SOL_SOCKET, Bt.SO_RCVTIMEO, ref value, sizeof(int)) == 0;
    }

    private static int WithSockaddr<T>(T address, Func<IntPtr, int> action) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(address, ptr, false);
            return action(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static SOCKADDR_BTH? ReadSockaddr(IntPtr socketHandle, bool peer)
    {
        var size = Marshal.SizeOf<SOCKADDR_BTH>();
        var length = size;
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            var rc = peer ? getpeername(socketHandle, ptr, ref length) : getsockname(socketHandle, ptr, ref length);
            return rc == 0 ? Marshal.PtrToStructure<SOCKADDR_BTH>(ptr) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}