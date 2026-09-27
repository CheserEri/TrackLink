using System.Runtime.InteropServices;

namespace TrackLink.Windows.Bluetooth;

/// <summary>
/// Windows 端 RFCOMM 服务端。
/// 走 Winsock <c>AF_BTH</c> + <c>BTHPROTO_RFCOMM</c>，并自行注册 SDP 记录，
/// 不使用 WinRT 的 <c>RfcommServiceProvider</c>（非打包 Win32 应用下能力声明受限）。
/// </summary>
internal sealed class RfcommHost : IDisposable
{
    /// <summary>服务 UUID，Android 端须用它来发现并连接。</summary>
    public static readonly Guid ServiceUuid = new("3e7a9c41-5b2d-4f88-9a16-7c4e0b13d592");

    public const string ServiceName = "TrackLink Touchpad Remote";
    private const int ListenBacklog = 2;

    private IntPtr _listenSocket = Bt.InvalidSocket;
    private Thread? _acceptThread;
    private volatile bool _running;
    private bool _serviceRegistered;
    private bool _wsaStarted;
    private BtSession? _currentSession;
    private volatile bool _lastSessionTimedOut;

    public ulong LocalAddress { get; private set; }

    /// <summary>
    /// 最近一次结束的会话是否因心跳超时（3 秒内没收到任何帧）而断。
    /// 界面靠它把「心跳超时」和「对端正常断开」区分开：<see cref="BtSession.Run"/> 的返回值
    /// 原本被丢弃，一旦会话引用被清成 null 就再也拿不到原因。
    /// </summary>
    public bool LastSessionTimedOut => _lastSessionTimedOut;

    public uint Channel { get; private set; }

    public bool ServiceRegistered => _serviceRegistered;

    /// <summary>当前活动会话；输入层把手势命令发到这里。没有连接时为 null。</summary>
    public BtSession? CurrentSession
    {
        get => Volatile.Read(ref _currentSession);
        private set
        {
            Volatile.Write(ref _currentSession, value);
            SessionChanged?.Invoke(value);
        }
    }

    public event Action<string>? Log;

    /// <summary>会话建立（非 null）与结束（null）时触发。</summary>
    public event Action<BtSession?>? SessionChanged;

    /// <summary>创建套接字、绑定通道、注册 SDP，并开始接受连接。失败时返回 false 并已输出原因。</summary>
    public bool Start()
    {
        var wsa = new WSADATA();
        if (Winsock.WSAStartup(0x0202, out wsa) != 0)
        {
            Log?.Invoke($"[错误] WSAStartup 失败，错误码 {Bt.LastError}");
            return false;
        }

        _wsaStarted = true;

        _listenSocket = Winsock.socket(Bt.AF_BTH, Bt.SOCK_STREAM, Bt.BTHPROTO_RFCOMM);
        if (_listenSocket == Bt.InvalidSocket)
        {
            Log?.Invoke($"[错误] 创建 AF_BTH 套接字失败，错误码 {Bt.LastError}（请确认蓝牙已开启）");
            return false;
        }

        // bind 阶段只用 addressFamily / btAddr / port：btAddr=0 表示绑定本机无线电，BT_PORT_ANY 让协议栈分配通道。
        var bindAddress = new SOCKADDR_BTH
        {
            addressFamily = Bt.AF_BTH,
            btAddr = 0,
            serviceClassId = Guid.Empty,
            port = Bt.BT_PORT_ANY,
        };

        if (Winsock.Bind(_listenSocket, bindAddress) != 0)
        {
            var error = Bt.LastError;
            Log?.Invoke(error == 10049
                ? $"[错误] bind 失败，错误码 {error}（WSAEADDRNOTAVAIL）：本机没有可用的蓝牙无线电，请确认蓝牙已开启。"
                : $"[错误] bind 失败，错误码 {error}");
            return false;
        }

        var local = Winsock.GetLocalAddress(_listenSocket);
        if (local is null)
        {
            Log?.Invoke($"[错误] getsockname 失败，错误码 {Bt.LastError}");
            return false;
        }

        LocalAddress = local.Value.btAddr;
        Channel = local.Value.port;

        if (LocalAddress == 0)
        {
            Log?.Invoke("[警告] 本机蓝牙地址读数为 0，可能蓝牙适配器未就绪。");
        }

        if (Winsock.listen(_listenSocket, ListenBacklog) != 0)
        {
            Log?.Invoke($"[错误] listen 失败，错误码 {Bt.LastError}");
            return false;
        }

        _serviceRegistered = RegisterService();

        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rfcomm-accept" };
        _acceptThread.Start();
        return true;
    }

    public void Stop()
    {
        if (!_running && _listenSocket == Bt.InvalidSocket)
        {
            return;
        }

        _running = false;

        if (_serviceRegistered)
        {
            StopService();
            _serviceRegistered = false;
        }

        if (_listenSocket != Bt.InvalidSocket)
        {
            // 关闭监听套接字会让阻塞中的 accept 立即返回错误，从而结束接受线程。
            Winsock.closesocket(_listenSocket);
            _listenSocket = Bt.InvalidSocket;
        }

        _acceptThread?.Join(TimeSpan.FromSeconds(2));
        _acceptThread = null;
        _lastSessionTimedOut = false;
    }

    public void Dispose()
    {
        Stop();

        if (_wsaStarted)
        {
            Winsock.WSACleanup();
            _wsaStarted = false;
        }
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            var client = Winsock.accept(_listenSocket, IntPtr.Zero, IntPtr.Zero);
            if (client == Bt.InvalidSocket)
            {
                if (!_running)
                {
                    break;
                }

                Log?.Invoke($"[错误] accept 失败，错误码 {Bt.LastError}");
                Thread.Sleep(200);
                continue;
            }

            var remote = Winsock.GetPeerAddress(client);
            using var session = new BtSession(client, remote?.btAddr ?? 0);
            session.Log += message => Log?.Invoke(message);
            _lastSessionTimedOut = false;
            CurrentSession = session;
            try
            {
                // 必须在清空 CurrentSession（会同步触发 SessionChanged）之前记下结束原因，
                // 否则界面只能看到「会话没了」，无法区分超时与正常断开。
                _lastSessionTimedOut = session.Run();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[错误] 会话异常：{ex.Message}");
            }
            finally
            {
                // 先摘掉引用再让 using 释放套接字，避免输入层向已关闭的会话发送命令。
                CurrentSession = null;
            }

            // 单手机场景下串行处理；会话结束后回到 accept，等待手机自动重连。
            Log?.Invoke("[服务端] 已回到监听状态，等待下一次连接。");
        }
    }

    private bool RegisterService()
    {
        var record = SdpRecordBuilder.BuildRfcommRecord(ServiceUuid, Channel, ServiceName);

        // BTH_SET_SERVICE 尾部是 pRecord[1]，故整块大小 = 结构大小 - 1 + 记录长度。
        var setServiceSize = Marshal.SizeOf<BTH_SET_SERVICE>() - 1 + record.Length;
        _sdpVersion = Marshal.AllocHGlobal(sizeof(uint));
        _sdpRecordHandle = Marshal.AllocHGlobal(IntPtr.Size);
        _sdpSetService = Marshal.AllocHGlobal(setServiceSize);
        _sdpBlob = Marshal.AllocHGlobal(Marshal.SizeOf<BLOB>());
        _sdpQuery = Marshal.AllocHGlobal(QuerySize);

        Marshal.WriteInt32(_sdpVersion, 0, unchecked((int)Bt.BTH_SDP_VERSION));
        // 新增注册要求 pRecordHandle 指向的值为 null；注册成功后系统会把句柄写回这里。
        Marshal.WriteIntPtr(_sdpRecordHandle, 0, IntPtr.Zero);

        Marshal.StructureToPtr(new BTH_SET_SERVICE
        {
            pSdpVersion = _sdpVersion,
            pRecordHandle = _sdpRecordHandle,
            ulRecordLength = (uint)record.Length,
        }, _sdpSetService, false);
        Marshal.Copy(record, 0, _sdpSetService + Marshal.SizeOf<BTH_SET_SERVICE>() - 1, record.Length);

        Marshal.StructureToPtr(new BLOB
        {
            cbSize = (uint)setServiceSize,
            pBlobData = _sdpSetService,
        }, _sdpBlob, false);

        var rc = SetService(Bt.RNRSERVICE_REGISTER);
        if (rc != 0)
        {
            Log?.Invoke($"[错误] SDP 服务注册失败，错误码 {Bt.LastError}");
            FreeServiceResources();
            return false;
        }

        var handle = Marshal.ReadIntPtr(_sdpRecordHandle, 0);
        Log?.Invoke($"[服务端] SDP 服务注册成功：UUID {ServiceUuid}，通道 {Channel}，记录句柄 0x{handle.ToInt64():X}");
        return true;
    }

    private bool StopService()
    {
        // 删除记录只需 WSAQUERYSET 的 dwSize / dwNameSpace / lpBlob，且必须用 RNRSERVICE_DELETE。
        var rc = SetService(Bt.RNRSERVICE_DELETE);
        if (rc != 0)
        {
            Log?.Invoke($"[警告] SDP 服务删除失败，错误码 {Bt.LastError}");
        }

        FreeServiceResources();
        return rc == 0;
    }

    /// <summary>按给定操作调用 <c>WSASetService</c>，手工铺 <c>WSAQUERYSET</c>（除 dwSize/dwNameSpace/lpBlob 外均被忽略）。</summary>
    private int SetService(uint operation)
    {
        Marshal.Copy(new byte[QuerySize], 0, _sdpQuery, QuerySize);
        WriteUInt32(_sdpQuery, 0, QuerySize);
        WriteUInt32(_sdpQuery, OffsetNameSpace, Bt.NS_BTH);
        WriteUInt32(_sdpQuery, OffsetNumberOfCsAddrs, 0);
        WritePointer(_sdpQuery, OffsetBlob, _sdpBlob);
        return Winsock.WSASetService(_sdpQuery, operation, 0);
    }

    private void FreeServiceResources()
    {
        Free(ref _sdpSetService);
        Free(ref _sdpBlob);
        Free(ref _sdpQuery);
        Free(ref _sdpVersion);
        Free(ref _sdpRecordHandle);

        static void Free(ref IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            Marshal.FreeHGlobal(pointer);
            pointer = IntPtr.Zero;
        }
    }

    // WSAQUERYSETW 在 x64 下的字段偏移（含对齐填充，共 120 字节）。
    private const int QuerySize = 120;
    private const int OffsetNameSpace = 40;
    private const int OffsetNumberOfCsAddrs = 88;
    private const int OffsetBlob = 112;

    private IntPtr _sdpSetService = IntPtr.Zero;
    private IntPtr _sdpBlob = IntPtr.Zero;
    private IntPtr _sdpQuery = IntPtr.Zero;
    private IntPtr _sdpVersion = IntPtr.Zero;
    private IntPtr _sdpRecordHandle = IntPtr.Zero;

    private static void WriteUInt32(IntPtr target, int offset, uint value) =>
        Marshal.WriteInt32(target, offset, unchecked((int)value));

    private static void WritePointer(IntPtr target, int offset, IntPtr value) =>
        Marshal.WriteIntPtr(target, offset, value);

    /// <summary>
    /// 按 UUID 查询 SDP 记录。传入 <paramref name="contextAddress"/> 为某台设备的蓝牙地址时，
    /// 查询的是该设备上的服务；传 null 表示本机已注册的服务记录。
    /// </summary>
    public static List<ServiceRecord> LookupService(Guid serviceUuid, string? contextAddress = null)
    {
        return Lookup(serviceUuid, contextAddress);
    }

    private static List<ServiceRecord> Lookup(Guid serviceUuid, string? contextAddress)
    {
        var results = new List<ServiceRecord>();
        var bufferSize = 4096;

        var pUuid = Marshal.AllocHGlobal(Marshal.SizeOf<Guid>());
        var pBuffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var uuidBytes = serviceUuid.ToByteArray();
            Marshal.Copy(uuidBytes, 0, pUuid, uuidBytes.Length);

            var query = new WSAQUERYSETW
            {
                dwSize = (uint)Marshal.SizeOf<WSAQUERYSETW>(),
                lpServiceClassId = pUuid,
                dwNameSpace = Bt.NS_BTH,
                lpszContext = contextAddress,
            };

            var flags = Bt.LUP_RETURN_ADDR | Bt.LUP_RETURN_NAME;

            var rc = Winsock.WSALookupServiceBegin(ref query, flags, out var handle);
            if (rc != 0)
            {
                return results;
            }

            try
            {
                while (true)
                {
                    var length = bufferSize;
                    if (Winsock.WSALookupServiceNext(handle, flags, ref length, pBuffer) != 0)
                    {
                        break;
                    }

                    var found = Marshal.PtrToStructure<WSAQUERYSETW>(pBuffer);
                    var name = found.lpszServiceInstanceName ?? "";
                    for (var i = 0; i < found.dwNumberOfCsAddrs; i++)
                    {
                        var info = Marshal.PtrToStructure<CSADDR_INFO>(found.lpcsaddrBuffer + i * Marshal.SizeOf<CSADDR_INFO>());

                        // 服务查询里「服务所在设备」既可能落在 LocalAddr 也可能落在 RemoteAddr，
                        // 取通道非 0 的那个作为有效结果。
                        var candidates = new[] { info.LocalAddr, info.RemoteAddr };
                        foreach (var candidate in candidates)
                        {
                            if (candidate.lpSockaddr == IntPtr.Zero || candidate.iSockaddrLength < Marshal.SizeOf<SOCKADDR_BTH>())
                            {
                                continue;
                            }

                            var sockaddr = Marshal.PtrToStructure<SOCKADDR_BTH>(candidate.lpSockaddr);
                            if (sockaddr.port != 0)
                            {
                                results.Add(new ServiceRecord(sockaddr.btAddr, sockaddr.port, name));
                            }
                        }
                    }
                }
            }
            finally
            {
                Winsock.WSALookupServiceEnd(handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pUuid);
            Marshal.FreeHGlobal(pBuffer);
        }

        return results;
    }
}

internal readonly record struct ServiceRecord(ulong Address, uint Channel, string Name);