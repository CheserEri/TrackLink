using System.Buffers.Binary;
using TrackLink.Windows.Bluetooth;

namespace TrackLink.Windows.Input;

internal enum RemoteCommandKind
{
    Tap,
    Scroll,
    Global,
    Move,
    DragBegin,
    DragMove,
    DragEnd,
}

/// <summary>全局操作的动作码，与 Android 端 <c>CommandPayload</c> 保持一致。</summary>
internal static class GlobalAction
{
    public const int Back = 1;
    public const int Home = 2;
    public const int Recents = 3;
}

/// <summary>
/// 一条要发给手机的控制命令，以及它在协议里的 payload 编码。
/// 坐标与位移均为归一化值：TAP 用坐标 0..65535，SCROLL / MOVE 用位移 i16（±32767 ≈ 一屏高/宽）。
/// </summary>
internal sealed record RemoteCommand(RemoteCommandKind Kind, int X, int Y, int Dx, int Dy, int Action)
{
    public static RemoteCommand Tap(int x, int y) => new(RemoteCommandKind.Tap, x, y, 0, 0, 0);

    public static RemoteCommand Scroll(int dx, int dy) => new(RemoteCommandKind.Scroll, 0, 0, dx, dy, 0);

    public static RemoteCommand Global(int action) => new(RemoteCommandKind.Global, 0, 0, 0, 0, action);

    public static RemoteCommand Move(int dx, int dy) => new(RemoteCommandKind.Move, 0, 0, dx, dy, 0);

    /// <summary>开始长按/拖动。坐标只在接收端光标尚未出现时作回退用。</summary>
    public static RemoteCommand DragBegin(int x, int y) => new(RemoteCommandKind.DragBegin, x, y, 0, 0, 0);

    public static RemoteCommand DragMove(int dx, int dy) => new(RemoteCommandKind.DragMove, 0, 0, dx, dy, 0);

    public static RemoteCommand DragEnd() => new(RemoteCommandKind.DragEnd, 0, 0, 0, 0, 0);

    public FrameType Type => Kind switch
    {
        RemoteCommandKind.Tap => FrameType.Tap,
        RemoteCommandKind.Scroll => FrameType.Scroll,
        RemoteCommandKind.Move => FrameType.Move,
        RemoteCommandKind.DragBegin => FrameType.DragBegin,
        RemoteCommandKind.DragMove => FrameType.DragMove,
        RemoteCommandKind.DragEnd => FrameType.DragEnd,
        _ => FrameType.Global,
    };

    public byte[] EncodePayload()
    {
        switch (Kind)
        {
            case RemoteCommandKind.Tap:
            case RemoteCommandKind.DragBegin:
                var point = new byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(point, (ushort)Math.Clamp(X, 0, 65535));
                BinaryPrimitives.WriteUInt16LittleEndian(point.AsSpan(2), (ushort)Math.Clamp(Y, 0, 65535));
                return point;

            case RemoteCommandKind.Scroll:
            case RemoteCommandKind.Move:
            case RemoteCommandKind.DragMove:
                var delta = new byte[4];
                BinaryPrimitives.WriteInt16LittleEndian(delta, (short)Math.Clamp(Dx, short.MinValue, short.MaxValue));
                BinaryPrimitives.WriteInt16LittleEndian(delta.AsSpan(2), (short)Math.Clamp(Dy, short.MinValue, short.MaxValue));
                return delta;

            case RemoteCommandKind.DragEnd:
                return [];

            default:
                return [(byte)Action];
        }
    }

    public string Describe() => Kind switch
    {
        RemoteCommandKind.Tap => $"TAP({X}, {Y})",
        RemoteCommandKind.Scroll => $"SCROLL(dx={Dx}, dy={Dy})",
        RemoteCommandKind.Move => $"MOVE(dx={Dx}, dy={Dy})",
        RemoteCommandKind.DragBegin => $"DRAG_BEGIN({X}, {Y})",
        RemoteCommandKind.DragMove => $"DRAG_MOVE(dx={Dx}, dy={Dy})",
        RemoteCommandKind.DragEnd => "DRAG_END",
        _ => $"GLOBAL({ActionName(Action)})",
    };

    private static string ActionName(int action) => action switch
    {
        GlobalAction.Back => "BACK",
        GlobalAction.Home => "HOME",
        GlobalAction.Recents => "RECENTS",
        _ => action.ToString(),
    };
}