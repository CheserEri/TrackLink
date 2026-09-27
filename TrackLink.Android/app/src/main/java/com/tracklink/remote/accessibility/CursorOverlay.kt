package com.tracklink.remote.accessibility

import android.accessibilityservice.AccessibilityService
import android.content.Context
import android.graphics.Color
import android.graphics.PixelFormat
import android.graphics.PointF
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.view.WindowManager

/**
 * 手机端鼠标光标。
 *
 * 用 `TYPE_ACCESSIBILITY_OVERLAY` 悬浮窗绘制：这个窗口类型专供无障碍服务使用，
 * **不需要 `SYSTEM_ALERT_WINDOW` 权限**（那是 `TYPE_APPLICATION_OVERLAY` 才需要的）。
 *
 * 窗口带 `FLAG_NOT_TOUCHABLE`，只负责显示、绝不抢触摸事件；
 * 点击仍由 [GestureDispatcher] 在光标坐标派发手势。
 *
 * 位置采用**相对累加**：Windows 端只发位移增量，这里累加到像素坐标上并钳制在屏幕内。
 * 绝对映射（触控板位置 ↔ 屏幕位置）会导致手指抬起后重新落下时光标瞬移，故不采用。
 *
 * 方法必须在主线程调用：`addView` / `updateViewLayout` 都要求带 Looper 的线程。
 */
object CursorOverlay {

    /** 归一化位移 ±32767 的满量程，与协议一致。 */
    private const val DELTA_MAX = 32767f

    /** 归一化坐标 0..65535 的满量程，与协议一致。 */
    private const val COORDINATE_MAX = 65535f

    private const val DIAMETER_DP = 22f
    private const val RING_DP = 2f

    /** 日志回调，由 [GestureDispatcher] 接到界面日志区。 */
    var log: ((String) -> Unit)? = null

    private var view: View? = null
    private var params: WindowManager.LayoutParams? = null
    private var windowManager: WindowManager? = null
    private var diameterPx = 0

    /** 光标圆心的屏幕像素坐标；<0 表示光标尚未出现。 */
    private var centerX = -1f
    private var centerY = -1f

    /** 光标圆心的屏幕像素坐标。光标尚未出现时为 null（此时点击应回退到帧里带的坐标）。 */
    val center: PointF?
        get() = if (centerX < 0f) null else PointF(centerX, centerY)

    /** 按归一化位移增量移动光标；首次调用时创建悬浮窗。 */
    fun moveBy(service: AccessibilityService, dx: Int, dy: Int) {
        val layout = ensureWindow(service) ?: return

        val metrics = service.resources.displayMetrics
        val half = diameterPx / 2f

        if (centerX < 0f) {
            // 首次出现从屏幕中心起步，避免光标从角落窜出来。
            centerX = metrics.widthPixels / 2f
            centerY = metrics.heightPixels / 2f
        }

        centerX = (centerX + dx / DELTA_MAX * metrics.widthPixels)
            .coerceIn(half, metrics.widthPixels - half)
        centerY = (centerY + dy / DELTA_MAX * metrics.heightPixels)
            .coerceIn(half, metrics.heightPixels - half)

        applyLayout(layout)
    }

    /**
     * 按归一化坐标定位光标，供**开始拖动**时兜底：拖动前若光标还没出现过，
     * 就先把它放到帧里带的位置上，免得触点落在屏幕中心而看不见光标。
     *
     * 光标已存在时保持不动——相对模式下帧里的坐标已不对应屏幕位置。
     *
     * @return 光标圆心的屏幕像素坐标；悬浮窗不可用时为 null。
     */
    fun ensureAt(service: AccessibilityService, nx: Int, ny: Int): PointF? {
        val layout = ensureWindow(service) ?: return null

        if (centerX < 0f) {
            val metrics = service.resources.displayMetrics
            val half = diameterPx / 2f
            centerX = (nx.coerceIn(0, COORDINATE_MAX.toInt()) / COORDINATE_MAX * metrics.widthPixels)
                .coerceIn(half, metrics.widthPixels - half)
            centerY = (ny.coerceIn(0, COORDINATE_MAX.toInt()) / COORDINATE_MAX * metrics.heightPixels)
                .coerceIn(half, metrics.heightPixels - half)
            applyLayout(layout)
        }

        return center
    }

    /** 把圆心坐标写进悬浮窗。位置没变则跳过，避免无谓的窗口更新。 */
    private fun applyLayout(layout: WindowManager.LayoutParams) {
        val half = diameterPx / 2f
        val px = (centerX - half).toInt()
        val py = (centerY - half).toInt()
        if (layout.x == px && layout.y == py) {
            return
        }

        layout.x = px
        layout.y = py
        runCatching { windowManager?.updateViewLayout(view, layout) }
    }

    /** 移除悬浮窗（无障碍服务被关闭时调用）。 */
    fun hide() {
        val target = view ?: return
        runCatching { windowManager?.removeView(target) }
        view = null
        params = null
        windowManager = null
        centerX = -1f
        centerY = -1f
    }

    private fun ensureWindow(service: AccessibilityService): WindowManager.LayoutParams? {
        params?.let { return it }

        val manager = service.getSystemService(Context.WINDOW_SERVICE) as? WindowManager ?: return null
        val density = service.resources.displayMetrics.density
        diameterPx = (DIAMETER_DP * density).toInt()

        // 半透明填充 + 深色描边：浅色与深色背景上都看得见。
        val dot = View(service).apply {
            background = GradientDrawable().apply {
                shape = GradientDrawable.OVAL
                setColor(Color.argb(70, 255, 255, 255))
                setStroke((RING_DP * density).toInt(), Color.argb(230, 20, 20, 20))
            }
        }

        val layout = WindowManager.LayoutParams(
            diameterPx,
            diameterPx,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                or WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE
                // 必须带上 IN_SCREEN：否则窗口坐标的原点是「状态栏下方」而不是物理屏幕顶部，
                // 而 dispatchGesture 用的是含状态栏的全屏坐标，圆球会比真实点击点低一个状态栏高度。
                or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.START
        }

        return try {
            manager.addView(dot, layout)
            view = dot
            params = layout
            windowManager = manager
            log?.invoke("手机端光标已启用。")
            layout
        } catch (t: Throwable) {
            log?.invoke("光标悬浮窗创建失败：${t.message ?: t.javaClass.simpleName}")
            null
        }
    }
}