using System.Text;

namespace TrackLink.Windows.Bluetooth;

/// <summary>
/// 手工构造 SDP 服务记录。Winsock 的 <c>WSASetService</c> 要求调用方给出完整的 SDP 记录字节流，
/// 只要记录里的 RFCOMM 通道号与实际监听的通道一致，Android 端就能按 UUID 发现并连接本服务。
/// </summary>
/// <remarks>
/// SDP 数据元素头部：类型占 bit 5..3，长度描述符占 bit 2..0（0..4 为定长，5 为后续 1 字节长度，
/// 6 为后续 2 字节长度）。因此 SEQUENCE+1字节长度 = 0x35，UUID16 = 0x19，UINT8 = 0x08。
/// </remarks>
internal static class SdpRecordBuilder
{
    // SDP 属性 ID
    private const ushort AttrServiceClassIdList = 0x0001;
    private const ushort AttrProtocolDescriptorList = 0x0004;
    private const ushort AttrBrowseGroupList = 0x0005;
    private const ushort AttrLanguageBaseAttributeIdList = 0x0006;
    private const ushort AttrBluetoothProfileDescriptorList = 0x0009;
    private const ushort AttrServiceName = 0x0100;

    // 协议 / 服务 UUID16
    private const ushort UuidL2cap = 0x0100;
    private const ushort UuidRfcomm = 0x0003;
    private const ushort UuidSerialPort = 0x1101;
    private const ushort UuidPublicBrowseGroup = 0x1002;

    private const byte HeaderSeq = 6 << 3;
    private const byte HeaderUuid = 3 << 3;
    private const byte HeaderText = 4 << 3;
    private const byte HeaderUInt = 1 << 3;

    /// <summary>构造一个串口轮廓（SPP）+ 自定义 UUID 的 RFCOMM 服务记录。</summary>
    public static byte[] BuildRfcommRecord(Guid serviceUuid, uint channel, string serviceName)
    {
        var serviceClassIdList = Seq(Uuid16(UuidSerialPort), Uuid128(serviceUuid));

        var protocolDescriptorList = Seq(
            Seq(Uuid16(UuidL2cap)),
            Seq(Uuid16(UuidRfcomm), UInt8((byte)channel)));

        var browseGroupList = Seq(Uuid16(UuidPublicBrowseGroup));

        // "en" + 偏移 0x006A（属性 ID 0x0100 的语言基址）+ 编码 UTF-8
        var languageBaseAttributeIdList = Seq(UInt16(0x656E), UInt16(0x006A), UInt16(0x0100));

        var profileDescriptorList = Seq(Seq(Uuid16(UuidSerialPort), UInt16(0x0102)));

        return Seq(
            Concat(UInt16(AttrServiceClassIdList), serviceClassIdList),
            Concat(UInt16(AttrProtocolDescriptorList), protocolDescriptorList),
            Concat(UInt16(AttrBrowseGroupList), browseGroupList),
            Concat(UInt16(AttrLanguageBaseAttributeIdList), languageBaseAttributeIdList),
            Concat(UInt16(AttrBluetoothProfileDescriptorList), profileDescriptorList),
            Concat(UInt16(AttrServiceName), Text(serviceName)));
    }

    private static byte[] Seq(params byte[][] items)
    {
        var body = Concat(items);
        if (body.Length < 0x100)
        {
            return Concat([(byte)(HeaderSeq | 5), (byte)body.Length], body);
        }

        return Concat([(byte)(HeaderSeq | 6), (byte)(body.Length >> 8), (byte)body.Length], body);
    }

    private static byte[] UInt8(byte value) => [(byte)(HeaderUInt | 0), value];

    private static byte[] UInt16(ushort value) => [(byte)(HeaderUInt | 1), (byte)(value >> 8), (byte)value];

    private static byte[] Uuid16(ushort value) => [(byte)(HeaderUuid | 1), (byte)(value >> 8), (byte)value];

    /// <summary>按 RFC 4122 大端字节序输出 128 位 UUID，与 Android 的 <c>UUID.fromString</c> 一致。</summary>
    private static byte[] Uuid128(Guid value)
    {
        var bytes = value.ToByteArray();
        Array.Reverse(bytes, 0, 4);
        Array.Reverse(bytes, 4, 2);
        Array.Reverse(bytes, 6, 2);
        return Concat([(byte)(HeaderUuid | 4)], bytes);
    }

    private static byte[] Text(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length >= 0x100)
        {
            throw new ArgumentException("服务名过长，超出 SDP 单字节长度上限。", nameof(value));
        }

        return Concat([(byte)(HeaderText | 5), (byte)bytes.Length], bytes);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var result = new byte[total];
        var offset = 0;
        foreach (var part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }

        return result;
    }
}