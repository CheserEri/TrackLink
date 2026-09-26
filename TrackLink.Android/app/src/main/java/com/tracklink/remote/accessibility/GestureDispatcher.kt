package com.tracklink.remote.accessibility

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import android.os.SystemClock
import android.util.DisplayMetrics
import kotlin.coroutines.resume
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull

/**
 * 手势派发器：把控制命令串行执行。
 *
 * `dispatchGesture()` 会取消正在进行的手势，因此同一时刻只能有一个手势在跑。
 * 这里用「单消费者 Channel」实现计划书要求的单一串行队列：消费者派发一条后，
 * 必须等它的 [AccessibilityService.GestureResultCallback] 回调返回才取下一条。
 *
 * 消费者跑在主线程（`Dispatchers.Main.immediate`）：`dispatchGesture` 与回调都在主线程，
 * 顺序确定；等待期间协程挂起，不会阻塞界面。
 */
object GestureDispatcher {

    /** 归一化坐标 0..65535 的满量程。 */
    private const val COORDINATE_MAX = 65535f

    /** 归一化滚动增量 ±32767 的满量程。 */
    private const val SCROLL_MAX = 32767f

    private const val TAP_DURATION_MS = 50L
    private const val SCROLL_DURATION_MS = 200L

    /** 手势回调未按预期返回时的兜底超时，保证队列能自愈（例如服务被中断）。 */
    private const val CALLBACK_GRACE_MS = 500L

    /** 派发日志（含耗时），由界面接管后写入日志区。 */
    var log: ((String) -> Unit)? = null

    private val queue = Channel<GestureCommand>(Channel.UNLIMITED)
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    @Volatile
    private var service: TrackLinkAccessibilityService? = null

    private var consumerStarted = false

    /** 无障碍服务当前是否可用。 */
    val enabled: Boolean get() = service != null

    fun attach(target: TrackLinkAccessibilityService) {
        service = target
        CursorOverlay.log = log
        DragGesture.log = log
        startConsumer()
        log?.invoke("无障碍服务已连接，手势队列就绪。")
    }

    fun detach(target: TrackLinkAccessibilityService) {
        if (service !== target) {
            return
        }

        service = null
        DragGesture.cancel("无障碍服务断开")
        CursorOverlay.hide()
        log?.invoke("无障碍服务已断开，后续命令将被丢弃。")
    }

    /** 系统中断时清空待执行命令（进行中的手势由系统自行取消）。 */
    fun onInterrupt() {
        DragGesture.cancel("无障碍服务被中断")

        var dropped = 0
        while (queue.tryReceive().isSuccess) {
            dropped++
        }

        if (dropped > 0) {
            log?.invoke("无障碍服务被中断，丢弃 $dropped 条待执行命令。")
        }
    }

    /** 入队一条命令；服务未启用时直接丢弃并记录原因。 */
    fun submit(command: GestureCommand) {
        val target = service
        if (target == null) {
            // 光标与拖动位移每秒可达几十条，服务未启用时静默丢弃，避免刷屏。
            if (!command.isHighFrequency()) {
                log?.invoke("忽略 ${command.describe()}：无障碍服务未启用。")
            }

            return
        }

        // 光标与拖动是一串**状态更新**，必须严格按到达顺序、且立刻生效：
        // 排在会阻塞的串行队列后面，拖动会明显滞后于手指；被点击/滚动挤在中间还会打断按住的手势。
        if (command.isPositionStream()) {
            scope.launch { execute(command) }
            return
        }

        queue.trySend(command)
    }

    /** 高频位移帧：不进日志，否则会把点/滚/返回的信息冲掉。 */
    private fun GestureCommand.isHighFrequency(): Boolean =
        this is GestureCommand.Move || this is GestureCommand.DragMove

    /** 光标与拖动共用的连续位置流，必须直通派发、不排队。 */
    private fun GestureCommand.isPositionStream(): Boolean =
        isHighFrequency() || this is GestureCommand.DragBegin || this is GestureCommand.DragEnd

    private fun startConsumer() {
        if (consumerStarted) {
            return
        }

        consumerStarted = true
        scope.launch {
            for (command in queue) {
                execute(command)
            }
        }
    }

    private suspend fun execute(command: GestureCommand) {
        val target = service ?: return

        when (command) {
            is GestureCommand.Global -> performGlobal(target, command)
            is GestureCommand.Tap -> dispatchGesture(
                target = target,
                description = buildTap(target, command),
                durationMs = TAP_DURATION_MS,
                label = command.describe(),
            )

            is GestureCommand.Scroll -> {
                val description = buildScroll(target, command)
                if (description == null) {
                    log?.invoke("忽略 ${command.describe()}：位移过小。")
                } else {
                    dispatchGesture(
                        target = target,
                        description = description,
                        durationMs = SCROLL_DURATION_MS,
                        label = command.describe(),
                    )
                }
            }

            is GestureCommand.Move -> CursorOverlay.moveBy(target, command.dx, command.dy)

            is GestureCommand.DragBegin -> {
                // 光标还没出现过就先按帧里的坐标把它摆好，免得触点和光标对不上。
                val point = CursorOverlay.ensureAt(target, command.x, command.y)
                if (point == null) {
                    log?.invoke("忽略 ${command.describe()}：光标悬浮窗不可用。")
                } else {
                    DragGesture.begin(target, point.x, point.y)
                }
            }

            is GestureCommand.DragMove -> {
                CursorOverlay.moveBy(target, command.dx, command.dy)
                DragGesture.follow()
            }

            GestureCommand.DragEnd -> DragGesture.end("抬手")
        }
    }

    private fun performGlobal(target: AccessibilityService, command: GestureCommand.Global) {
        val action = when (command.action) {
            1 -> AccessibilityService.GLOBAL_ACTION_BACK
            2 -> AccessibilityService.GLOBAL_ACTION_HOME
            3 -> AccessibilityService.GLOBAL_ACTION_RECENTS
            else -> null
        }

        if (action == null) {
            log?.invoke("忽略 ${command.describe()}：未知全局操作。")
            return
        }

        val startedAt = SystemClock.elapsedRealtime()
        val ok = target.performGlobalAction(action)
        val cost = SystemClock.elapsedRealtime() - startedAt
        log?.invoke("${command.describe()} → ${if (ok) "已执行" else "系统拒绝"}（耗时 $cost ms）")
    }

    private suspend fun dispatchGesture(
        target: AccessibilityService,
        description: GestureDescription,
        durationMs: Long,
        label: String,
    ) {
        val startedAt = SystemClock.elapsedRealtime()
        val outcome = withTimeoutOrNull(durationMs + CALLBACK_GRACE_MS) {
            awaitGesture(target, description)
        }

        val cost = SystemClock.elapsedRealtime() - startedAt
        val text = when (outcome) {
            true -> "已执行"
            false -> "系统拒绝"
            null -> "回调超时"
        }
        log?.invoke("$label → $text（耗时 $cost ms）")
    }

    private suspend fun awaitGesture(
        target: AccessibilityService,
        description: GestureDescription,
    ): Boolean = suspendCancellableCoroutine { continuation ->
        val callback = object : AccessibilityService.GestureResultCallback() {
            override fun onCompleted(gestureDescription: GestureDescription?) {
                if (continuation.isActive) {
                    continuation.resume(true)
                }
            }

            override fun onCancelled(gestureDescription: GestureDescription?) {
                if (continuation.isActive) {
                    continuation.resume(false)
                }
            }
        }

        val accepted = target.dispatchGesture(description, callback, null)
        if (!accepted && continuation.isActive) {
            continuation.resume(false)
        }
    }

    /** 单点手势：笔画长度必须非 0，否则 `GestureDescription` 会判为非法路径。 */
    private fun buildTap(target: AccessibilityService, command: GestureCommand.Tap): GestureDescription {
        val metrics = target.resources.displayMetrics

        // 光标走的是相对位移，手指在触控板的位置已不对应屏幕位置，
        // 因此只要光标出现过就以光标为准；否则回退到帧里带的绝对坐标。
        val cursor = CursorOverlay.center
        val x = cursor?.x ?: normalize(command.x, COORDINATE_MAX, metrics.widthPixels)
        val y = cursor?.y ?: normalize(command.y, COORDINATE_MAX, metrics.heightPixels)

        val path = Path().apply {
            moveTo(x, y)
            lineTo(x + 0.5f, y + 0.5f)
        }

        return GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0L, TAP_DURATION_MS))
            .build()
    }

    /**
     * 滚动手势：从屏幕中心沿位移较大的那条轴滑动。
     * 返回 null 表示位移小于 1 像素，不值得派发。
     */
    private fun buildScroll(
        target: AccessibilityService,
        command: GestureCommand.Scroll,
    ): GestureDescription? {
        val metrics = target.resources.displayMetrics
        val width = metrics.widthPixels.toFloat()
        val height = metrics.heightPixels.toFloat()

        val vertical = abs(command.dy) >= abs(command.dx)
        val dx = if (vertical) 0f else command.dx / SCROLL_MAX * width
        val dy = if (vertical) command.dy / SCROLL_MAX * height else 0f

        if (abs(dx) < 1f && abs(dy) < 1f) {
            return null
        }

        val path = Path().apply {
            moveTo(width / 2f, height / 2f)
            lineTo(width / 2f + dx, height / 2f + dy)
        }

        return GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0L, SCROLL_DURATION_MS))
            .build()
    }

    private fun normalize(value: Int, max: Float, pixels: Int): Float =
        (value.coerceIn(0, max.toInt()) / max) * pixels

    /** 供界面读取屏幕尺寸做自测提示（点击回中心时用得到）。 */
    fun screenMetrics(): DisplayMetrics? = service?.resources?.displayMetrics
}