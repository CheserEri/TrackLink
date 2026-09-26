using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TrackLink.Windows.Bluetooth;

/// <summary>第 2 周只用到握手与心跳；第 3 周加入控制类命令（TAP/SCROLL/GLOBAL），第 4 周加入光标与拖动。</summary>
internal enum FrameType : byte
{
    Hello = 0x01,

    /// <summary>点击：payload [x:u16][y:u16]，归一化 0..65535。</summary>
    Tap = 0x10,

    /// <summary>滚动：payload [dx:i16][dy:i16]，±32767 ≈ 一屏高/宽。</summary>
    Scroll = 0x11,

    /// <summary>全局操作：payload [action:u8]，1=BACK 2=HOME 3=RECENTS。</summary>
    Global = 0x12,

    /// <summary>光标移动：payload [dx:i16][dy:i16]，±32767 ≈ 一屏宽/高。频率高，接收端不进日志。</summary>
    Move = 0x13,

    /// <summary>开始长按/拖动：payload [x:u16][y:u16]，仅当接收端光标尚未出现时作为回退坐标。</summary>
    DragBegin = 0x14,

    /// <summary>拖动中的位移：payload [dx:i16][dy:i16]，与 Scroll 同量纲。</summary>
    DragMove = 0x15,

    /// <summary>结束长按/拖动（抬手）：无 payload。</summary>
    DragEnd = 0x16,

    /// <summary>停止：仅定义，不实现。</summary>
    Stop = 0x1F,

    Ping = 0x30,
    Pong = 0x31,
}

internal readonly struct Frame(FrameType type, uint sequence, byte[] payload)
{
    public readonly FrameType Type = type;
    public readonly uint Sequence = sequence;
    public readonly byte[] Payload = payload;
}

/// <summary>帧格式：[magic:2B][version:1B][type:1B][sequence:4B(LE)][length:2B(LE)][payload]。</summary>
internal static class Protocol
{
    public const byte Magic0 = 0x54; // 'T'
    public const byte Magic1 = 0x4C; // 'L'
    public const byte Version = 0x01;
    public const int HeaderLength = 10;
    public const int MaxPayload = 1024;

    public static byte[] Encode(FrameType type, uint sequence, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[HeaderLength + payload.Length];
        buffer[0] = Magic0;
        buffer[1] = Magic1;
        buffer[2] = Version;
        buffer[3] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), (ushort)payload.Length);
        payload.CopyTo(buffer.AsSpan(HeaderLength));
        return buffer;
    }
}

/// <summary>
/// RFCOMM 是字节流，没有消息边界。这里做增量解析：magic 之前或长度不合法的垃圾数据直接丢弃。
/// </summary>
internal sealed class FrameParser
{
    private readonly List<byte> _buffer = [];

    public void Feed(ReadOnlySpan<byte> data, List<Frame> output)
    {
        foreach (var b in data)
        {
            _buffer.Add(b);
        }

        while (true)
        {
            var start = IndexOfMagic();
            if (start < 0)
            {
                // 保留末尾一个字节，避免 magic 首字节恰好落在本次数据末尾而被丢掉。
                if (_buffer.Count > 1)
                {
                    _buffer.RemoveRange(0, _buffer.Count - 1);
                }

                return;
            }

            if (start > 0)
            {
                _buffer.RemoveRange(0, start);
            }

            if (_buffer.Count < Protocol.HeaderLength)
            {
                return;
            }

            var span = CollectionsMarshal.AsSpan(_buffer);
            if (_buffer[2] != Protocol.Version)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            int length = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);
            if (length > Protocol.MaxPayload)
            {
                _buffer.RemoveAt(0);
                continue;
            }

            if (_buffer.Count < Protocol.HeaderLength + length)
            {
                return;
            }

            var payload = new byte[length];
            _buffer.CopyTo(Protocol.HeaderLength, payload, 0, length);
            output.Add(new Frame(
                (FrameType)_buffer[3],
                BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
                payload));
            _buffer.RemoveRange(0, Protocol.HeaderLength + length);
        }
    }

    private int IndexOfMagic()
    {
        for (var i = 0; i + 1 < _buffer.Count; i++)
        {
            if (_buffer[i] == Protocol.Magic0 && _buffer[i + 1] == Protocol.Magic1)
            {
                return i;
            }
        }

        return -1;
    }
}