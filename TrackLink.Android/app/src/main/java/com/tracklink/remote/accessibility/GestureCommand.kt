package com.tracklink.remote.accessibility

/**
 * Windows 端 `RemoteCommand.cs` 对应的三类控制命令。
 *
 * 坐标一律是归一化值，由 Android 侧再映射到屏幕像素。
 */
sealed interface GestureCommand {
    /** 点击。x / y 为归一化坐标 0..65535。 */
    data class Tap(val x: Int, val y: Int) : GestureCommand

    /** 滚动。dx / dy 为归一化增量，±32767 ≈ 一屏宽/高。 */
    data class Scroll(val dx: Int, val dy: Int) : GestureCommand

    /** 全局操作。action 见 `CommandPayload.GLOBAL_*`。 */
    data class Global(val action: Int) : GestureCommand

    /** 光标移动增量。dx / dy 为归一化位移，±32767 ≈ 一屏宽/高。频率高，不进日志。 */
    data class Move(val dx: Int, val dy: Int) : GestureCommand

    /**
     * 开始长按/拖动：在屏幕上按下并保持一个触点。
     * x / y 为归一化坐标 0..65535，只在光标尚未出现时用于定位（相对模式下不代表屏幕位置）。
     */
    data class DragBegin(val x: Int, val y: Int) : GestureCommand

    /** 拖动中的位移增量，量纲同 [Move]。频率高，不进日志。 */
    data class DragMove(val dx: Int, val dy: Int) : GestureCommand

    /** 结束长按/拖动（抬手）。 */
    data object DragEnd : GestureCommand
}

/** 供日志与界面展示。 */
fun GestureCommand.describe(): String = when (this) {
    is GestureCommand.Tap -> "TAP($x, $y)"
    is GestureCommand.Scroll -> "SCROLL($dx, $dy)"
    is GestureCommand.Global -> "GLOBAL(${actionName(action)})"
    is GestureCommand.Move -> "MOVE($dx, $dy)"
    is GestureCommand.DragBegin -> "DRAG_BEGIN($x, $y)"
    is GestureCommand.DragMove -> "DRAG_MOVE($dx, $dy)"
    GestureCommand.DragEnd -> "DRAG_END"
}

private fun actionName(action: Int): String = when (action) {
    1 -> "BACK"
    2 -> "HOME"
    3 -> "RECENTS"
    else -> action.toString()
}