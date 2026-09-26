using System.Buffers.Binary;

namespace TrackLink.Windows.Bluetooth;

/// <summary>
/// 一条已建立的 RFCOMM 会话：发送 HELLO 握手，按 1 秒周期发 PING，
/// 3 秒内收不到任何帧即判定链路失效并主动断开。
/// </summary>
internal sealed class BtSession(IntPtr socket, ulong remoteAddress) : IDisposable
{
    private const int PingIntervalMs = 1000;
    private const int TimeoutMs = 3000;
    private const int ReceiveBufferSize = 4096;

    private readonly object _sendLock = new();
    private long _lastReceiveTicks = DateTime.UtcNow.Ticks;
    private volatile bool _alive = true;
    private volatile bool _timedOut;
    private int _closed;
    private uint _txSequence;

    public ulong RemoteAddress { get; } = remoteAddress;

    public event Action<string>? Log;

    /// <summary>每次心跳收到 PONG 时给出往返毫秒数，供界面实时显示（不解析日志文本）。</summary>
    public event Action<long>? Rtt;

    /// <summary>阻塞处理本会话，直到对端关闭、出错或心跳超时。返回 true 表示因心跳超时结束。</summary>
    public bool Run()
    {
        var heartbeat = new Thread(HeartbeatLoop) { IsBackground = true, Name = "rfcomm-heartbeat" };
        heartbeat.Start();

        Log?.Invoke($"[连接] 远端设备 {Bt.FormatAddress(RemoteAddress)}");
        Send(FrameType.Hello, [Protocol.Version]);

        var parser = new FrameParser();
        var buffer = new byte[ReceiveBufferSize];
        var frames = new List<Frame>();

        while (_alive)
        {
            var received = Winsock.recv(socket, buffer, buffer.Length, 0);
            if (received <= 0)
            {
                if (_alive && !_timedOut)
                {
                    Log?.Invoke(received == 0
                        ? "[断开] 对端正常关闭连接。"
                        : $"[断开] 接收失败，错误码 {Bt.LastError}");
                }

                break;
            }

            Volatile.Write(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);

            frames.Clear();
            parser.Feed(buffer.AsSpan(0, received), frames);
            foreach (var frame in frames)
            {
                Handle(frame);
            }
        }

        _alive = false;
        heartbeat.Join(TimeSpan.FromSeconds(1));

        if (_timedOut)
        {
            Log?.Invoke($"[断开] 心跳超时（{TimeoutMs} ms 内未收到任何帧），已断开链路。");
        }

        Log?.Invoke("[断开] 会话结束。");
        return _timedOut;
    }

    private void Handle(Frame frame)
    {
        switch (frame.Type)
        {
            case FrameType.Hello:
                var version = frame.Payload.Length > 0 ? frame.Payload[0] : (byte)0;
                Log?.Invoke(version == Protocol.Version
                    ? $"[握手] 对端协议版本 {version}，一致。"
                    : $"[握手] 对端协议版本 {version}，与本端 {Protocol.Version} 不一致，建议升级其中一端。");
                break;

            case FrameType.Ping:
                Send(FrameType.Pong, frame.Payload);
                break;

            case FrameType.Pong:
                if (frame.Payload.Length >= 8)
                {
                    var sentAt = BinaryPrimitives.ReadInt64LittleEndian(frame.Payload);
                    var rtt = Environment.TickCount64 - sentAt;
                    Rtt?.Invoke(rtt);
                    Log?.Invoke($"[心跳] PONG 往返 {rtt} ms");
                }
                else
                {
                    Log?.Invoke("[心跳] PONG");
                }

                break;

            case FrameType.Stop:
                Log?.Invoke("[帧] 收到 STOP：本周未实现，已忽略。");
                break;

            default:
                Log?.Invoke($"[帧] 忽略未知类型 0x{(byte)frame.Type:X2}，序号 {frame.Sequence}。");
                break;
        }
    }

    private void HeartbeatLoop()
    {
        while (_alive)
        {
            Thread.Sleep(PingIntervalMs);
            if (!_alive)
            {
                return;
            }

            var idleMs = (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastReceiveTicks)) / TimeSpan.TicksPerMillisecond;
            if (idleMs > TimeoutMs)
            {
                _timedOut = true;
                _alive = false;
                CloseSocket();
                return;
            }

            var payload = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(payload, Environment.TickCount64);
            Send(FrameType.Ping, payload);
        }
    }

    /// <summary>发送一帧控制命令或心跳帧。返回 false 表示会话已关闭或写入失败。</summary>
    public bool Send(FrameType type, byte[] payload)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return false;
        }

        var frame = Protocol.Encode(type, unchecked(_txSequence++), payload);
        lock (_sendLock)
        {
            var sent = 0;
            while (sent < frame.Length)
            {
                var written = Winsock.send(socket, frame[sent..], frame.Length - sent, 0);
                if (written <= 0)
                {
                    Log?.Invoke($"[错误] 发送失败，错误码 {Bt.LastError}");
                    return false;
                }

                sent += written;
            }
        }

        return true;
    }

    private void CloseSocket()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            Winsock.closesocket(socket);
        }
    }

    public void Dispose() => CloseSocket();
}