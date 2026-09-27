package com.tracklink.remote.bluetooth

import android.annotation.SuppressLint
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.SystemClock
import androidx.core.content.ContextCompat
import androidx.core.content.IntentCompat
import com.tracklink.remote.accessibility.GestureCommand
import com.tracklink.remote.accessibility.describe
import java.io.IOException
import java.util.UUID
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

enum class LinkState { IDLE, CONNECTING, CONNECTED, FAILED }

data class PeerDevice(
    val name: String,
    val address: String,
    val bonded: Boolean,
) {
    val displayName: String get() = if (bonded) "$name（已配对）" else name
}

data class LinkUiState(
    val state: LinkState = LinkState.IDLE,
    val peer: PeerDevice? = null,
    val peers: List<PeerDevice> = emptyList(),
    val rttMs: Long? = null,
    val error: String? = null,
    val scanning: Boolean = false,
    val enabled: Boolean = false,
    val log: List<String> = emptyList(),
    /**
     * 是否因为心跳超时而落到 FAILED。
     *
     * 单独开一个布尔值而不是让界面去比对 error 文案：超时（笔记本 3 秒没回应）和
     * 连接失败（配对/通道问题）要给用户看完全不同的反馈，靠字符串匹配迟早会踩坑。
     * 与 Windows 端 `RemoteEngine.Phase` 的 Timeout 语义一致。
     */
    val timedOut: Boolean = false,
)

/**
 * 第 2 周：Android 端 RFCOMM 客户端。
 *
 * 职责：找到已配对的笔记本 → 按 UUID 建连 → 收发帧 → 1 秒 PING 心跳 / 3 秒超时断线。
 * 与 Windows 端 `RfcommHost` + `BtSession` 对称。
 */
class BluetoothConnectionManager(
    private val context: Context,
    private val scope: CoroutineScope,
) {
    companion object {
        /** 与 Windows 端 `RfcommHost.ServiceUuid` 必须完全一致（小写标准形式）。 */
        const val SERVICE_UUID_STRING = "3e7a9c41-5b2d-4f88-9a16-7c4e0b13d592"
        val SERVICE_UUID: UUID = UUID.fromString(SERVICE_UUID_STRING)

        const val PING_INTERVAL_MS = 1000L
        const val TIMEOUT_MS = 3000L
        private const val DISCOVERY_DURATION_MS = 12_000L
        private const val MAX_LOG_LINES = 200
    }

    private val _state = MutableStateFlow(LinkUiState())
    val state: StateFlow<LinkUiState> = _state.asStateFlow()

    /**
     * 控制命令出口，由界面接到 `GestureDispatcher.submit`。
     * 未接线时命令被直接丢弃（例如界面已销毁）。
     */
    var commandHandler: ((GestureCommand) -> Unit)? = null

    private val adapter: BluetoothAdapter? =
        (context.getSystemService(Context.BLUETOOTH_SERVICE) as? BluetoothManager)?.adapter

    private val sendLock = Any()
    private var socket: BluetoothSocket? = null
    private var sessionJob: Job? = null
    private var discoveryReceiver: BroadcastReceiver? = null
    private var sequence = 0
    private var lastReceiveMs = 0L

    @Volatile
    private var manualClose = false

    /** BLUETOOTH_CONNECT 已授予时才可调用；未授予时返回 false。 */
    @SuppressLint("MissingPermission")
    fun refresh(): Boolean {
        val bluetoothAdapter = adapter
        val enabled = bluetoothAdapter?.isEnabled == true
        _state.update { it.copy(enabled = enabled) }

        if (bluetoothAdapter == null) {
            log("本机没有可用的蓝牙适配器。")
            return false
        }

        if (!enabled) {
            log("蓝牙未开启，请在系统设置里打开蓝牙。")
            return false
        }

        val bonded = bluetoothAdapter.bondedDevices
            ?.map { PeerDevice(it.name ?: "(未命名)", it.address, true) }
            .orEmpty()
            .sortedBy { it.name }
        mergePeers(bonded)
        log("已配对设备 ${bonded.size} 台。")
        return true
    }

    @SuppressLint("MissingPermission")
    fun startDiscovery(): Boolean {
        val bluetoothAdapter = adapter ?: return false
        if (bluetoothAdapter.isEnabled.not()) return false

        stopDiscovery()
        val receiver = object : BroadcastReceiver() {
            @SuppressLint("MissingPermission")
            override fun onReceive(receiverContext: Context, intent: Intent) {
                if (intent.action != BluetoothDevice.ACTION_FOUND) return
                val device = IntentCompat.getParcelableExtra(
                    intent,
                    BluetoothDevice.EXTRA_DEVICE,
                    BluetoothDevice::class.java,
                ) ?: return
                mergePeers(
                    listOf(
                        PeerDevice(
                            device.name ?: "(未命名)",
                            device.address,
                            device.bondState == BluetoothDevice.BOND_BONDED,
                        ),
                    ),
                )
            }
        }

        ContextCompat.registerReceiver(
            context,
            receiver,
            IntentFilter(BluetoothDevice.ACTION_FOUND),
            ContextCompat.RECEIVER_NOT_EXPORTED,
        )
        discoveryReceiver = receiver

        bluetoothAdapter.startDiscovery()
        _state.update { it.copy(scanning = true) }
        log("开始扫描周围蓝牙设备（约 ${DISCOVERY_DURATION_MS / 1000} 秒）…")

        scope.launch {
            delay(DISCOVERY_DURATION_MS)
            stopDiscovery()
        }
        return true
    }

    @SuppressLint("MissingPermission")
    fun stopDiscovery() {
        if (adapter?.isDiscovering == true) {
            adapter?.cancelDiscovery()
        }

        discoveryReceiver?.let { runCatching { context.unregisterReceiver(it) } }
        discoveryReceiver = null
        _state.update { it.copy(scanning = false) }
    }

    fun connect(peer: PeerDevice) {
        if (sessionJob?.isActive == true) {
            log("已有会话在运行，先断开再连接。")
            return
        }

        manualClose = false
        // 会话全程是阻塞 I/O（connect/read/write），必须离开主线程，否则界面会卡死。
        sessionJob = scope.launch(Dispatchers.IO) { runConnection(peer) }
    }

    fun disconnect() {
        manualClose = true
        val job = sessionJob
        sessionJob = null
        closeQuietly(socket)
        socket = null

        scope.launch {
            job?.cancelAndJoin()
            _state.update {
                it.copy(state = LinkState.IDLE, peer = null, rttMs = null, error = null, timedOut = false)
            }
            log("已断开连接。")
        }
    }

    @SuppressLint("MissingPermission")
    private suspend fun runConnection(peer: PeerDevice) {
        val bluetoothAdapter = adapter ?: return
        var sessionSocket: BluetoothSocket? = null

        _state.update {
            it.copy(state = LinkState.CONNECTING, peer = peer, rttMs = null, error = null, timedOut = false)
        }
        log("正在连接 ${peer.name} (${peer.address}) …")

        try {
            sessionSocket = withContext(Dispatchers.IO) {
                bluetoothAdapter.cancelDiscovery()
                val device = bluetoothAdapter.getRemoteDevice(peer.address)
                val sock = device.createRfcommSocketToServiceRecord(SERVICE_UUID)
                val startedAt = SystemClock.elapsedRealtime()
                sock.connect()
                log("连接建立，耗时 ${SystemClock.elapsedRealtime() - startedAt} ms。")
                sock
            }

            socket = sessionSocket
            lastReceiveMs = SystemClock.elapsedRealtime()
            _state.update { it.copy(state = LinkState.CONNECTED) }
            runSession(sessionSocket)
        } catch (t: Throwable) {
            if (!manualClose) {
                val reason = t.message ?: t.javaClass.simpleName
                log("错误：$reason")
                _state.update {
                    it.copy(state = LinkState.FAILED, error = reason, rttMs = null)
                }
            }
        } finally {
            closeQuietly(sessionSocket)
            if (socket === sessionSocket) {
                socket = null
            }
        }
    }

    private suspend fun runSession(sessionSocket: BluetoothSocket) = coroutineScope {
        // 继承调用方的 IO 上下文；用结构化并发保证会话结束时心跳一定被取消。
        val heartbeat = launch { heartbeatLoop(sessionSocket) }

        try {
            send(sessionSocket, FrameType.HELLO, byteArrayOf(Protocol.VERSION))

            val input = sessionSocket.inputStream
            val parser = FrameParser()
            val buffer = ByteArray(4096)

            while (currentCoroutineContext().isActive) {
                val read = input.read(buffer)
                if (read <= 0) {
                    if (!manualClose) log("对端关闭了连接。")
                    break
                }

                lastReceiveMs = SystemClock.elapsedRealtime()
                for (frame in parser.feed(buffer, 0, read)) {
                    handle(sessionSocket, frame)
                }
            }
        } catch (t: Throwable) {
            if (!manualClose) log("读取中断：${t.message ?: t.javaClass.simpleName}")
        } finally {
            heartbeat.cancel()
        }
    }

    private suspend fun heartbeatLoop(sessionSocket: BluetoothSocket) {
        while (true) {
            delay(PING_INTERVAL_MS)

            if (SystemClock.elapsedRealtime() - lastReceiveMs > TIMEOUT_MS) {
                log("心跳超时（${TIMEOUT_MS} ms 内未收到任何帧），主动断开链路。")
                _state.update {
                    it.copy(
                        state = LinkState.FAILED,
                        error = "心跳超时",
                        rttMs = null,
                        timedOut = true,
                    )
                }
                // 关掉套接字让阻塞中的 read 立刻返回，从而结束整个会话。
                closeQuietly(sessionSocket)
                return
            }

            send(sessionSocket, FrameType.PING, tickPayload())
        }
    }

    private fun handle(sessionSocket: BluetoothSocket, frame: Frame) {
        when (frame.type) {
            FrameType.HELLO -> {
                val version = frame.payload.firstOrNull() ?: 0
                log(
                    if (version == Protocol.VERSION) {
                        "握手：对端协议版本 $version，一致。"
                    } else {
                        "握手：对端协议版本 $version，与本端 ${Protocol.VERSION} 不一致。"
                    },
                )
            }

            FrameType.PING -> send(sessionSocket, FrameType.PONG, frame.payload)

            FrameType.PONG -> {
                if (frame.payload.size >= 8) {
                    val rtt = SystemClock.elapsedRealtime() - readI64(frame.payload)
                    _state.update { it.copy(rttMs = rtt) }
                }
            }

            FrameType.TAP -> if (acceptCommand(frame, CommandPayload.TAP_SIZE)) {
                emit(
                    GestureCommand.Tap(
                        CommandPayload.tapX(frame.payload),
                        CommandPayload.tapY(frame.payload),
                    ),
                    frame,
                )
            }

            FrameType.SCROLL -> if (acceptCommand(frame, CommandPayload.SCROLL_SIZE)) {
                emit(
                    GestureCommand.Scroll(
                        CommandPayload.scrollDx(frame.payload),
                        CommandPayload.scrollDy(frame.payload),
                    ),
                    frame,
                )
            }

            FrameType.GLOBAL -> if (acceptCommand(frame, CommandPayload.GLOBAL_SIZE)) {
                emit(GestureCommand.Global(frame.payload[0].toInt() and 0xFF), frame)
            }

            FrameType.MOVE -> if (acceptCommand(frame, CommandPayload.MOVE_SIZE)) {
                emit(
                    GestureCommand.Move(
                        CommandPayload.scrollDx(frame.payload),
                        CommandPayload.scrollDy(frame.payload),
                    ),
                    frame,
                )
            }

            FrameType.DRAG_BEGIN -> if (acceptCommand(frame, CommandPayload.DRAG_BEGIN_SIZE)) {
                emit(
                    GestureCommand.DragBegin(
                        CommandPayload.tapX(frame.payload),
                        CommandPayload.tapY(frame.payload),
                    ),
                    frame,
                )
            }

            FrameType.DRAG_MOVE -> if (acceptCommand(frame, CommandPayload.DRAG_MOVE_SIZE)) {
                emit(
                    GestureCommand.DragMove(
                        CommandPayload.scrollDx(frame.payload),
                        CommandPayload.scrollDy(frame.payload),
                    ),
                    frame,
                )
            }

            FrameType.DRAG_END -> if (acceptCommand(frame, CommandPayload.DRAG_END_SIZE)) {
                emit(GestureCommand.DragEnd, frame)
            }

            FrameType.STOP -> log("收到 STOP：未实现，已忽略。")
        }
    }

    /** 校验命令帧长度，避免越界读取；不符时丢弃并记录。 */
    private fun acceptCommand(frame: Frame, expectedSize: Int): Boolean {
        if (frame.payload.size == expectedSize) {
            return true
        }

        log("忽略 ${frame.type.name}：payload 长度 ${frame.payload.size}，期望 $expectedSize。")
        return false
    }

    private fun emit(command: GestureCommand, frame: Frame) {
        val handler = commandHandler
        if (handler == null) {
            log("收到 ${command.describe()}，但界面未接线，已丢弃。")
            return
        }

        // 光标与拖动位移每秒可达几十条，进日志会把点/滚/返回的信息冲掉。
        if (command !is GestureCommand.Move && command !is GestureCommand.DragMove) {
            log("收到 ${command.describe()}（序号 ${frame.sequence}）")
        }

        handler.invoke(command)
    }

    /** 供外部（例如手势派发器）写入同一条日志流。 */
    fun appendLog(message: String) = log(message)

    private fun send(sessionSocket: BluetoothSocket, type: FrameType, payload: ByteArray) {
        val frame = Protocol.encode(type, sequence++, payload)
        try {
            synchronized(sendLock) {
                sessionSocket.outputStream.write(frame)
                sessionSocket.outputStream.flush()
            }
        } catch (t: IOException) {
            if (!manualClose) log("发送 ${type.name} 失败：${t.message ?: t.javaClass.simpleName}")
        }
    }

    private fun tickPayload(): ByteArray {
        val now = SystemClock.elapsedRealtime()
        return ByteArray(8) { index -> ((now shr (index * 8)) and 0xFF).toByte() }
    }

    private fun readI64(payload: ByteArray): Long {
        var value = 0L
        for (index in 0 until 8) {
            value = value or ((payload[index].toLong() and 0xFF) shl (index * 8))
        }
        return value
    }

    private fun closeQuietly(target: BluetoothSocket?) {
        runCatching { target?.close() }
    }

    private fun mergePeers(incoming: List<PeerDevice>) {
        _state.update { current ->
            val byAddress = LinkedHashMap<String, PeerDevice>()
            for (peer in incoming + current.peers) {
                val existing = byAddress[peer.address]
                if (existing == null || (peer.bonded && !existing.bonded)) {
                    byAddress[peer.address] = peer
                }
            }
            current.copy(peers = byAddress.values.sortedWith(compareByDescending<PeerDevice> { it.bonded }.thenBy { it.name }))
        }
    }

    private fun log(message: String) {
        val stamp = SystemClock.elapsedRealtime()
        _state.update { it.copy(log = (listOf("[$stamp] $message") + it.log).take(MAX_LOG_LINES)) }
    }
}