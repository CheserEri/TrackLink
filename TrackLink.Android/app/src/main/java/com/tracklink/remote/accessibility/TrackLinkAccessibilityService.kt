package com.tracklink.remote.accessibility

import android.accessibilityservice.AccessibilityService
import android.view.accessibility.AccessibilityEvent

/**
 * TrackLink 无障碍服务：只负责执行点击、滑动与全局操作，不读取屏幕内容。
 *
 * 手势本身由 [GestureDispatcher] 串行派发，本服务只做生命周期登记，
 * 这样蓝牙侧无需关心服务的创建与销毁时机。
 */
class TrackLinkAccessibilityService : AccessibilityService() {

    override fun onServiceConnected() {
        super.onServiceConnected()
        GestureDispatcher.attach(this)
    }

    /** 本项目不需要读取界面内容，刻意留空。 */
    override fun onAccessibilityEvent(event: AccessibilityEvent?) = Unit

    override fun onInterrupt() {
        GestureDispatcher.onInterrupt()
    }

    override fun onDestroy() {
        GestureDispatcher.detach(this)
        super.onDestroy()
    }
}