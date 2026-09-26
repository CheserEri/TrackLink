package com.tracklink.remote.bluetooth

import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 与 Windows 端 `TrackLink.Windows/Bluetooth/Protocol.cs` 逐字节一致。
 *
 * 帧格式：[magic:2B('T','L')][version:1B=0x01][type:1B][sequence:4B LE][length:2B LE][payload]
 */
enum class FrameType(val code: Byte) {
    HELLO(0x01),

    /** 点击：payload [x:u16][y:u16]，归一化 0..65535。 */
    TAP(0x10),

    /** 滚动：payload [dx:i16][dy:i16]，±32767 ≈ 一屏高/宽。 */
    SCROLL(0x11),

    /** 全局操作：payload [action:u8]，见 [CommandPayload.GLOBAL_BACK] 等。 */
    GLOBAL(0x12),

    /** 光标移动：payload [dx:i16][dy:i16]，±32767 ≈ 一屏宽/高（相对模式，累加到位移上）。频率高，不进日志。 */
    MOVE(0x13),

    /** 开始长按/拖动：payload [x:u16][y:u16]，仅当光标尚未出现时作回退坐标。 */
    DRAG_BEGIN(0x14),

    /** 拖动中的位移：payload [dx:i16][dy:i16]，与 SCROLL 同量纲。频率高，不进日志。 */
    DRAG_MOVE(0x15),

    /** 结束长按/拖动（抬手）：无 payload。 */
    DRAG_END(0x16),

    /** 停止：仅定义，不实现。 */
    STOP(0x1F),

    PING(0x30),
    PONG(0x31),
    ;

    companion object {
        fun of(code: Byte): FrameType? {
            for (type in values()) {
                if (type.code == code) return type
            }
            return null
        }
    }
}

class Frame(val type: FrameType, val sequence: Int, val payload: ByteArray)

object Protocol {
    const val MAGIC0: Byte = 0x54 // 'T'
    const val MAGIC1: Byte = 0x4C // 'L'
    const val VERSION: Byte = 0x01
    const val HEADER_LENGTH = 10
    const val MAX_PAYLOAD = 1024

    fun encode(type: FrameType, sequence: Int, payload: ByteArray): ByteArray {
        val buffer = ByteBuffer.allocate(HEADER_LENGTH + payload.size).order(ByteOrder.LITTLE_ENDIAN)
        buffer.put(MAGIC0)
        buffer.put(MAGIC1)
        buffer.put(VERSION)
        buffer.put(type.code)
        buffer.putInt(sequence)
        buffer.putShort(payload.size.toShort())
        buffer.put(payload)
        return buffer.array()
    }
}

/**
 * 控制类帧的 payload 布局（一律小端），与 Windows 端 `RemoteCommand.cs` 逐字节一致。
 *
 * Android 本周只解码、不发送，故这里只提供读取函数。
 */
object CommandPayload {
    const val TAP_SIZE = 4
    const val MOVE_SIZE = 4
    const val SCROLL_SIZE = 4
    const val GLOBAL_SIZE = 1
    const val DRAG_BEGIN_SIZE = 4
    const val DRAG_MOVE_SIZE = 4
    const val DRAG_END_SIZE = 0

    const val GLOBAL_BACK = 1
    const val GLOBAL_HOME = 2
    const val GLOBAL_RECENTS = 3

    // TAP 是归一化坐标点；MOVE 与 SCROLL 同为归一化位移增量，共用读取函数。
    fun tapX(payload: ByteArray): Int = readU16(payload, 0)
    fun tapY(payload: ByteArray): Int = readU16(payload, 2)
    fun scrollDx(payload: ByteArray): Int = readI16(payload, 0)
    fun scrollDy(payload: ByteArray): Int = readI16(payload, 2)

    fun readU16(payload: ByteArray, at: Int): Int =
        (payload[at].toInt() and 0xFF) or ((payload[at + 1].toInt() and 0xFF) shl 8)

    fun readI16(payload: ByteArray, at: Int): Int = readU16(payload, at).toShort().toInt()
}

/**
 * RFCOMM 是字节流，没有消息边界。与 Windows 端同样做增量解析：
 * magic 之前或长度不合法的垃圾数据逐字节丢弃。
 */
class FrameParser {
    private var buffer = ByteArray(4096)
    private var length = 0

    fun feed(data: ByteArray, offset: Int = 0, size: Int = data.size): List<Frame> {
        ensureCapacity(length + size)
        System.arraycopy(data, offset, buffer, length, size)
        length += size

        val frames = ArrayList<Frame>()
        while (true) {
            val start = indexOfMagic()
            if (start < 0) {
                // 保留末尾一个字节，避免 magic 首字节恰好落在本次数据末尾而被丢掉。
                if (length > 1) discard(length - 1)
                return frames
            }

            if (start > 0) discard(start)
            if (length < Protocol.HEADER_LENGTH) return frames

            if (buffer[2] != Protocol.VERSION) {
                discard(1)
                continue
            }

            val payloadLength = readU16(8)
            if (payloadLength > Protocol.MAX_PAYLOAD) {
                discard(1)
                continue
            }

            if (length < Protocol.HEADER_LENGTH + payloadLength) return frames

            val type = FrameType.of(buffer[3])
            if (type != null) {
                frames.add(
                    Frame(
                        type,
                        readI32(4),
                        buffer.copyOfRange(
                            Protocol.HEADER_LENGTH,
                            Protocol.HEADER_LENGTH + payloadLength,
                        ),
                    ),
                )
            }

            discard(Protocol.HEADER_LENGTH + payloadLength)
        }
    }

    private fun indexOfMagic(): Int {
        for (i in 0 until length - 1) {
            if (buffer[i] == Protocol.MAGIC0 && buffer[i + 1] == Protocol.MAGIC1) return i
        }
        return -1
    }

    private fun readU16(at: Int): Int =
        (buffer[at].toInt() and 0xFF) or ((buffer[at + 1].toInt() and 0xFF) shl 8)

    private fun readI32(at: Int): Int =
        (buffer[at].toInt() and 0xFF) or
            ((buffer[at + 1].toInt() and 0xFF) shl 8) or
            ((buffer[at + 2].toInt() and 0xFF) shl 16) or
            (buffer[at + 3].toInt() shl 24)

    private fun discard(count: Int) {
        if (count >= length) {
            length = 0
            return
        }

        System.arraycopy(buffer, count, buffer, 0, length - count)
        length -= count
    }

    private fun ensureCapacity(required: Int) {
        if (required <= buffer.size) return
        var size = buffer.size
        while (size < required) size *= 2
        buffer = buffer.copyOf(size)
    }
}