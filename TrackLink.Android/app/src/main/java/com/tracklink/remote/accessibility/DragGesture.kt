package com.tracklink.remote.accessibility

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.graphics.Path
import kotlin.math.abs
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * 长按 / 拖动：在手机屏幕上维持一个**真实按住不放**的触点。
 *
 * `dispatchGesture()` 派发的普通手势到时长就走完并抬手，表达不了「一直按住」。
 * 这里用 `StrokeDescription(willContinue = true)`：笔画时长走完后触点**不抬起**，
 * 再用 `continueStroke()` 沿「上一个笔画的终点 → 当前光标位置」续接，把触点挂在屏幕上。
 * 被控 App 按「按住的时长与位移」自行决定这是长按还是拖动，本端不做区分。
 *
 * 笔画时长取 [STROKE_MS]（1 ms）：位移立即生效，免得触点按插值慢慢爬。
 * 保活定时器每 [KEEPALIVE_MS] 续接一次，防止长时间不动时触点被系统悄悄抬起。
 *
 * 续接必须**连续**派发：中间一旦插进别的 `dispatchGesture`（例如 [GestureDispatcher] 里的
 * 点击/滚动），系统会把手势取消、触点抬起。因此拖动相关的帧在派发器里走的是不排队的直通路径。
 *
 * 所有方法都在主线程调用（同 [GestureDispatcher]）。
 */
object DragGesture {

    /** 单个笔画的时长。取极小值＝触点立刻到达新位置，不沿路径慢慢插值。 */
    private const val STROKE_MS = 1L

    /** 保活续接间隔。触点不动时也要定期续接一次。 */
    private const val KEEPALIVE_MS = 100L

    /** 位移不足这么多像素就不单独派发一次续接，交给保活定时器捎带。 */
    private const val MIN_STEP_PX = 1f

    /** 日志回调，由 [GestureDispatcher] 接到界面日志区。 */
    var log: ((String) -> Unit)? = null

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private var service: AccessibilityService? = null
    private var stroke: GestureDescription.StrokeDescription? = null
    private var keepAlive: Job? = null

    /** 触点当前所在位置（与 [CursorOverlay.center] 同步）。 */
    private var x = 0f
    private var y = 0f

    /** 屏幕上此刻是否真的按着一个触点。 */
    val active: Boolean get() = stroke != null

    /** 在给定像素坐标处落下触点并保持。 */
    fun begin(target: AccessibilityService, px: Float, py: Float) {
        if (stroke != null) {
            end("重新开始长按/拖动")
        }

        service = target
        x = px
        y = py

        val first = GestureDescription.StrokeDescription(path(px, py, px, py), 0L, STROKE_MS, true)
        if (!dispatch(first, "开始长按/拖动")) {
            clear()
            log?.invoke("长按/拖动未能开始：系统拒绝了手势。")
            return
        }

        stroke = first
        log?.invoke("长按/拖动已按下（${px.toInt()}, ${py.toInt()}）。")
        startKeepAlive()
    }

    /** 光标移动后调用：把触点续接到最新位置。位置没怎么变时不动，由保活兜底。 */
    fun follow() {
        val cursor = CursorOverlay.center ?: return
        if (abs(cursor.x - x) < MIN_STEP_PX && abs(cursor.y - y) < MIN_STEP_PX) {
            return
        }

        continueTo(cursor.x, cursor.y, willContinue = true, action = "拖动位移")
    }

    /** 抬手，结束长按/拖动。 */
    fun end(reason: String) {
        val current = stroke ?: return
        val cursor = CursorOverlay.center
        val toX = cursor?.x ?: x
        val toY = cursor?.y ?: y

        val last = current.continueStroke(path(x, y, toX, toY), 0L, STROKE_MS, false)
        dispatch(last, "结束长按/拖动")
        clear()
        log?.invoke("长按/拖动已抬起（$reason）。")
    }

    /**
     * 只丢弃状态、不派发抬手。用于无障碍服务断开或系统中断——
     * 这两种情况下手势已经被系统取消，触点早就抬起来了。
     */
    fun cancel(reason: String) {
        if (stroke == null) {
            return
        }

        clear()
        log?.invoke("长按/拖动被取消（$reason）。")
    }

    private fun startKeepAlive() {
        keepAlive?.cancel()
        keepAlive = scope.launch {
            while (isActive && stroke != null) {
                delay(KEEPALIVE_MS)
                val cursor = CursorOverlay.center ?: continue
                continueTo(cursor.x, cursor.y, willContinue = true, action = "保活续接")
            }
        }
    }

    /** 从当前终点续接到新位置；`willContinue` 为 false 即抬手。 */
    private fun continueTo(toX: Float, toY: Float, willContinue: Boolean, action: String): Boolean {
        val current = stroke ?: return false
        val next = current.continueStroke(path(x, y, toX, toY), 0L, STROKE_MS, willContinue)

        if (!dispatch(next, action)) {
            // 续接失败说明触点已经不在屏幕上了，留着状态只会误导后续判定。
            clear()
            log?.invoke("长按/拖动续接失败，已放弃。")
            return false
        }

        stroke = next
        x = toX
        y = toY
        return true
    }

    private fun dispatch(stroke: GestureDescription.StrokeDescription, action: String): Boolean {
        val target = service ?: return false
        val description = GestureDescription.Builder().addStroke(stroke).build()
        val ok = runCatching { target.dispatchGesture(description, null, null) }.getOrDefault(false)
        if (!ok) {
            log?.invoke("$action：系统拒绝了手势。")
        }

        return ok
    }

    private fun clear() {
        keepAlive?.cancel()
        keepAlive = null
        stroke = null
        service = null
    }

    /** 笔画路径：从上一个笔画的终点连到新位置。长度可以为 0（原地按住）。 */
    private fun path(fromX: Float, fromY: Float, toX: Float, toY: Float): Path = Path().apply {
        moveTo(fromX, fromY)
        lineTo(toX, toY)
    }
}