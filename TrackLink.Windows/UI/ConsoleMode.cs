using System.Buffers.Binary;
using TrackLink.Windows.Bluetooth;
using TrackLink.Windows.Input;

namespace TrackLink.Windows.UI;

/// <summary>
/// 控制台模式（<c>selftest</c> / <c>serve</c> / <c>remote</c>）。
///
/// 图形界面是默认入口，这些模式保留下来做回归验证与链路自检：
/// 免开界面就能确认「服务端能起、SDP 能查、手势能识别、命令能发」。
/// 代码从原 <c>Program.cs</c> 原样搬迁，逻辑未改。
/// </summary>
internal static class ConsoleMode
{
    private const int LoopbackConnectTimeoutMs = 8000;

    public static int PrintUsage()
    {
        Console.WriteLine("TrackLink · Windows 端（图形界面 + RFCOMM 服务端）");
        Console.WriteLine("用法：");
        Console.WriteLine("  TrackLink.Windows            打开图形界面（默认）");
        Console.WriteLine("  TrackLink.Windows selftest   自检：启动服务端 + 回查 SDP + 本机回环 PING/PONG");
        Console.WriteLine("  TrackLink.Windows serve      只启动服务端，等待 Android 真机连接（Ctrl+C 退出）");
        Console.WriteLine("  TrackLink.Windows remote     服务端 + 触控板手势识别，把手势命令发给手机（Ctrl+C 退出）");
        Console.WriteLine("  TrackLink.Windows help       显示本帮助");
        return 0;
    }

    /// <summary>控制台遥控模式：蓝牙服务端 + 触控板监听 + 手势识别 → 发送控制命令。</summary>
    public static int RunRemote()
    {
        Console.WriteLine("TrackLink · 控制台遥控模式（触控板 → 蓝牙 → 手机）");
        Console.WriteLine(new string('-', 78));

        using var host = new RfcommHost();
        host.Log += Console.WriteLine;

        if (!host.Start())
        {
            Console.WriteLine("[remote] ✗ 蓝牙服务端启动失败，无法继续。");
            return 1;
        }

        Console.WriteLine($"[remote] 蓝牙服务端就绪：地址 {Bt.FormatAddress(host.LocalAddress)}，"
                          + $"RFCOMM 通道 {host.Channel}，SDP 注册 {(host.ServiceRegistered ? "成功" : "失败")}");

        using var listener = new RawInputListener();
        listener.Log += Console.WriteLine;

        if (!listener.Start())
        {
            Console.WriteLine("[remote] ✗ 触控板监听启动失败，无法继续。");
            return 1;
        }

        var decoder = listener.PrimaryDecoder;
        if (decoder is null)
        {
            Console.WriteLine("[remote] ✗ 未找到可用的触控板解析器，无法继续。");
            return 1;
        }

        Console.WriteLine($"[remote] 触控板分组键 {listener.TouchpadGroupKey}，"
                          + $"归一化范围 X {decoder.XRange.Min}..{decoder.XRange.Max} / "
                          + $"Y {decoder.YRange.Min}..{decoder.YRange.Max}"
                          + $"（来源：{(decoder.UsingFallbackRanges ? "第 1 周实测回退值" : "能力表 LogicalMin/Max")}）");

        var settings = Settings.AppSettings.Load();
        var recognizer = new GestureRecognizer(decoder.XRange, decoder.YRange, settings.Gesture);
        recognizer.Log += Console.WriteLine;
        recognizer.Command += command => SendCommand(host, command);

        host.SessionChanged += session => Console.WriteLine(session is null
            ? "[remote] 手机已断开，手势命令将被丢弃。"
            : $"[remote] 手机已连接（{Bt.FormatAddress(session.RemoteAddress)}），开始转发手势命令。");

        listener.Report += recognizer.OnReport;

        // 触控板每上报一份报文就记一次时间戳，供本机鼠标闸门判定「这条鼠标输入是不是触控板来的」。
        listener.Report += _ => LocalMouseBlocker.NoteTouchpadReport();

        // 屏蔽触控板在本机的副作用：系统触控板栈会用同一份上报合成鼠标输入，
        // 于是手机在动的同时笔记本光标也在动、轻点还会在本机点一下。
        Console.WriteLine(LocalMouseBlocker.Start(out var error)
            ? "[触控板] 本机鼠标闸门已开启：触控板不再带动本机光标、也不在本机点击（外接鼠标不受影响）。"
            : $"[触控板] ✗ 本机鼠标闸门开启失败（{error}），本机光标仍会跟着手指动。");

        Console.WriteLine(new string('-', 78));
        Console.WriteLine("手势：单指移动 = 移动手机光标；单指轻点 / 触控板物理按压 = 点击；"
                          + "双击后按住不放 = 长按（按住后滑动即拖动）；"
                          + "双指上下滑 = 滚动；三指横滑（左右均可）= 返回上一页；三指轻点 = 返回桌面");
        Console.WriteLine("请先在手机端连接 TrackLink 并启用「TrackLink 触控板遥控」无障碍服务。按 Ctrl+C 退出。");
        Console.WriteLine(new string('-', 78));

        var stopped = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.Set();
        };

        try
        {
            stopped.Wait();
        }
        finally
        {
            LocalMouseBlocker.Stop();
        }

        Console.WriteLine("[remote] 正在停止…");
        return 0;
    }

    /// <summary>
    /// 把识别出的命令发给当前会话，并打印发送时刻（用于比对端到端延迟）。
    /// 连续位置流（MOVE / DRAG_MOVE）每秒可达几十条，不逐条打印，否则会把手势小结刷掉。
    /// </summary>
    private static void SendCommand(RfcommHost host, RemoteCommand command)
    {
        // 拖动中的位移帧与纯光标帧一样高频。
        var quiet = command.Kind is RemoteCommandKind.Move or RemoteCommandKind.DragMove;

        var session = host.CurrentSession;
        if (session is null)
        {
            if (!quiet)
            {
                Console.WriteLine($"[{Environment.TickCount64} ms] 无活动会话，丢弃 {command.Describe()}");
            }

            return;
        }

        if (quiet)
        {
            session.Send(command.Type, command.EncodePayload());
            return;
        }

        var sentAt = Environment.TickCount64;
        var ok = session.Send(command.Type, command.EncodePayload());
        Console.WriteLine($"[{sentAt} ms] 发送 {command.Describe()} → {(ok ? "成功" : "失败")}");
    }

    public static int RunServer()
    {
        using var host = new RfcommHost();
        host.Log += Console.WriteLine;

        if (!host.Start())
        {
            return 1;
        }

        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"[服务端] 本机蓝牙地址 {Bt.FormatAddress(host.LocalAddress)}，RFCOMM 通道 {host.Channel}");
        Console.WriteLine($"[服务端] 服务 UUID {RfcommHost.ServiceUuid}");
        Console.WriteLine("[服务端] 请让手机与笔记本先在系统设置里完成蓝牙配对，再运行 Android 端程序连接。");
        Console.WriteLine("[服务端] 按 Ctrl+C 退出。");
        Console.WriteLine(new string('-', 78));

        var stopped = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopped.Set();
        };

        stopped.Wait();
        Console.WriteLine("[服务端] 正在停止…");
        return 0;
    }

    public static int RunSelfTest()
    {
        Console.WriteLine("TrackLink · 蓝牙链路自检");
        Console.WriteLine(new string('-', 78));

        using var host = new RfcommHost();
        host.Log += Console.WriteLine;

        if (!host.Start())
        {
            Console.WriteLine("[自检] ✗ 服务端启动失败，蓝牙链路无法继续验证。");
            return 1;
        }

        Console.WriteLine(new string('-', 78));
        Console.WriteLine($"[自检] ① 服务端启动成功：地址 {Bt.FormatAddress(host.LocalAddress)}，通道 {host.Channel}，" +
                          $"SDP 注册 {(host.ServiceRegistered ? "成功" : "失败")}");
        Console.WriteLine(new string('-', 78));

        var discovered = CheckSdpLookup(host);

        Console.WriteLine(new string('-', 78));
        Console.WriteLine("[自检] ③ 本机回环连接（Windows 蓝牙栈允许连接自身地址）");
        var loopbackOk = CheckLoopback(host);

        Console.WriteLine(new string('-', 78));
        Console.WriteLine("自检结论");
        Console.WriteLine($"  ① 服务端绑定与监听      : {(host.Channel != 0 ? "通过" : "失败")}（通道 {host.Channel}）");
        Console.WriteLine($"  ② SDP 记录可被按 UUID 查询: {(discovered ? "通过" : "未通过")}");
        Console.WriteLine($"  ③ 回环建立连接并收到 PONG : {(loopbackOk ? "通过" : "未通过")}");
        Console.WriteLine(discovered && loopbackOk
            ? "  → Windows 端蓝牙链路目标达成，可进入 Android 端对接。"
            : "  → Windows 侧仍有未通过项；若蓝牙栈不允许连接自身地址，第 ③ 项需改用 Android 真机验证。");
        return discovered && loopbackOk ? 0 : 2;
    }

    /// <summary>按 UUID 回查本机 SDP 记录，验证 Android 侧按 UUID 发现服务所依赖的注册确实生效。</summary>
    private static bool CheckSdpLookup(RfcommHost host)
    {
        Console.WriteLine("[自检] ② 按 UUID 回查 SDP 记录");

        var attempts = new (string Label, string? Context)[]
        {
            ("以本机地址为上下文", Bt.FormatAddress(host.LocalAddress)),
            ("不指定上下文", null),
        };

        var found = false;
        foreach (var (label, context) in attempts)
        {
            var results = RfcommHost.LookupService(RfcommHost.ServiceUuid, context);
            if (results.Count == 0)
            {
                Console.WriteLine($"  {label}：查询无结果（错误码 {Bt.LastError}）");
                continue;
            }

            foreach (var record in results)
            {
                Console.WriteLine($"  {label}：地址 {Bt.FormatAddress(record.Address)}，通道 {record.Channel}，名称 \"{record.Name}\"");
                found = true;
            }
        }

        return found;
    }

    /// <summary>连接自身蓝牙地址做回环，验证「连接 → 帧收发 → PONG」整条链路。</summary>
    private static bool CheckLoopback(RfcommHost host)
    {
        var socket = Winsock.socket(Bt.AF_BTH, Bt.SOCK_STREAM, Bt.BTHPROTO_RFCOMM);
        if (socket == Bt.InvalidSocket)
        {
            Console.WriteLine($"  创建客户端套接字失败，错误码 {Bt.LastError}");
            return false;
        }

        try
        {
            Winsock.SetRecvTimeout(socket, LoopbackConnectTimeoutMs);

            var target = new SOCKADDR_BTH
            {
                addressFamily = Bt.AF_BTH,
                btAddr = host.LocalAddress,
                serviceClassId = RfcommHost.ServiceUuid,
                port = host.Channel,
            };

            var connectStarted = Environment.TickCount64;
            if (Winsock.Connect(socket, target) != 0)
            {
                Console.WriteLine($"  连接自身地址失败，错误码 {Bt.LastError}" +
                                  "（若为 10060/10065 等超时或不可达，属于蓝牙栈不支持自身回环，请用 Android 真机验证）。");
                return false;
            }

            Console.WriteLine($"  连接建立，耗时 {Environment.TickCount64 - connectStarted} ms，开始发送 PING");

            var sequence = 0u;
            for (var i = 0; i < 3; i++)
            {
                var payload = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(payload, Environment.TickCount64);
                var frame = Protocol.Encode(FrameType.Ping, sequence++, payload);
                if (Winsock.send(socket, frame, frame.Length, 0) <= 0)
                {
                    Console.WriteLine($"  发送 PING 失败，错误码 {Bt.LastError}");
                    return false;
                }

                if (!TryReadPong(socket, out var rtt))
                {
                    Console.WriteLine($"  等待 PONG 失败，错误码 {Bt.LastError}");
                    return false;
                }

                Console.WriteLine($"  第 {i + 1} 次 PING → PONG，往返 {rtt} ms");
            }

            return true;
        }
        finally
        {
            Winsock.closesocket(socket);
        }
    }

    private static bool TryReadPong(IntPtr socket, out long rtt)
    {
        rtt = 0;
        var parser = new FrameParser();
        var buffer = new byte[4096];
        var frames = new List<Frame>();
        var deadline = Environment.TickCount64 + LoopbackConnectTimeoutMs;

        while (Environment.TickCount64 < deadline)
        {
            var received = Winsock.recv(socket, buffer, buffer.Length, 0);
            if (received <= 0)
            {
                return false;
            }

            frames.Clear();
            parser.Feed(buffer.AsSpan(0, received), frames);
            foreach (var frame in frames)
            {
                if (frame.Type != FrameType.Pong || frame.Payload.Length < 8)
                {
                    continue;
                }

                rtt = Environment.TickCount64 - BinaryPrimitives.ReadInt64LittleEndian(frame.Payload);
                return true;
            }
        }

        return false;
    }
}