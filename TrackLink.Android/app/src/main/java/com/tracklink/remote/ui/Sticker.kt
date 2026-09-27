package com.tracklink.remote.ui

import android.graphics.drawable.AnimatedImageDrawable
import android.os.Build
import android.widget.ImageView
import androidx.annotation.DrawableRes
import androidx.annotation.RequiresApi
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.scale
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.graphics.drawable.toBitmap
import com.tracklink.remote.R

/**
 * 表情贴纸的语义键。界面只按含义引用，具体图由 [StickerKey.res] 决定，
 * 与 Windows 端 `Sticker.cs` 的键一一对应。
 */
enum class StickerKey(@DrawableRes val res: Int) {
    /** 启动欢迎（派蒙开心，动图）。 */
    welcome(R.drawable.st_welcome),

    /** 等待手机连接（双手合十祈祷）。 */
    waiting(R.drawable.st_waiting),

    /** 连接中（派蒙晕眩，动图）。 */
    connecting(R.drawable.st_connecting),

    /** 已连接成功（抱面包幸福）。 */
    connected(R.drawable.st_connected),

    /** 服务已就绪 / 启动成功（举拳自信）。 */
    ready(R.drawable.st_ready),

    /** 检测到设备（出击闪光）。 */
    found(R.drawable.st_found),

    /** 扫描 / 检测中（兰那罗飞行）。 */
    searching(R.drawable.st_searching),

    /** 空列表 / 未发现设备（疑惑）。 */
    nothing(R.drawable.st_nothing),

    /** 已暂停（喝茶休息）。 */
    paused(R.drawable.st_paused),

    /** 手势识别正常（闭眼哼歌）。 */
    great(R.drawable.st_great),

    /** 连接断开 / 失败（一人睡着、一人震惊）。 */
    lost(R.drawable.st_lost),

    /** 心跳超时（用户指定的「麻烦」大图，唯一用途）。 */
    timeout(R.drawable.st_timeout),

    /** 权限 / 无障碍未启用（刻晴：我拒绝）。 */
    rejected(R.drawable.st_rejected),

    /** 提示 / 疑问（兰那罗疑问）。 */
    hint(R.drawable.st_hint),

    /** 等待 / 处理中（七七输入中）。 */
    busy(R.drawable.st_busy),

    /** 退出 / 已停止（北斗：拜拜）。 */
    bye(R.drawable.st_bye),

    /** 异常警告（冰史莱姆惊，动图）。 */
    shock(R.drawable.st_shock),
}

/** 每张贴纸配一句人话说明，显示在贴纸下方。 */
val StickerKey.caption: String
    get() = when (this) {
        StickerKey.welcome -> "欢迎使用 TrackLink"
        StickerKey.waiting -> "等待手机连接"
        StickerKey.connecting -> "正在连接…"
        StickerKey.connected -> "已连接"
        StickerKey.ready -> "服务已就绪"
        StickerKey.found -> "已抓到设备"
        StickerKey.searching -> "正在扫描…"
        StickerKey.nothing -> "列表还是空的"
        StickerKey.paused -> "已暂停（喝口茶）"
        StickerKey.great -> "手势识别正常"
        StickerKey.lost -> "连接断开了"
        StickerKey.timeout -> "心跳超时：3 秒内没收到笔记本的任何回应"
        StickerKey.rejected -> "权限被拒绝了"
        StickerKey.hint -> "看一眼这里"
        StickerKey.busy -> "处理中…"
        StickerKey.bye -> "已停止"
        StickerKey.shock -> "出问题了！"
    }

/**
 * 显示一张表情贴纸。
 *
 * GIF 在 API 28+ 用 [AnimatedImageDrawable] + [AndroidView] 真播放（测试机 Redmi K20 Pro / Android 11 支持）；
 * 更低版本或静态图走 Compose 的 [Image]，解码失败时给一张透明占位，绝不崩界面。
 *
 * [key] 变化时重播一次「弹出 + 淡入」：动画状态用 key 作为 remember 的键，换图天然重新入场，
 * 也不会因为每秒重组而反复重播。
 */
@Composable
fun Sticker(
    key: StickerKey,
    size: Dp,
    modifier: Modifier = Modifier,
) {
    val context = LocalContext.current
    val drawable = remember(key) { ContextCompat.getDrawable(context, key.res) }

    val enter = remember(key) { Animatable(0f) }
    LaunchedEffect(key) {
        enter.snapTo(0f)
        enter.animateTo(1f, tween(durationMillis = 380, easing = FastOutSlowInEasing))
    }

    val progress = enter.value
    val animated = modifier
        .size(size)
        .scale(0.82f + 0.18f * progress)
        .alpha(progress)

    when {
        // API 27 及以下系统根本没有 AnimatedImageDrawable，这里必须先判版本再碰这个类。
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && drawable is AnimatedImageDrawable ->
            AnimatedSticker(drawable, animated)

        else -> {
            val bitmap = remember(key) { drawable?.toBitmap()?.asImageBitmap() }
            if (bitmap != null) {
                Image(
                    bitmap = bitmap,
                    contentDescription = key.caption,
                    contentScale = ContentScale.Fit,
                    alignment = Alignment.Center,
                    modifier = animated,
                )
            }
        }
    }
}

/**
 * 真的会动的 GIF。单独一个 @RequiresApi(P) 函数，是为了让 API 26/27 的设备
 * 在类校验阶段就不会碰到 AnimatedImageDrawable 的成员引用。
 */
@RequiresApi(Build.VERSION_CODES.P)
@Composable
private fun AnimatedSticker(drawable: AnimatedImageDrawable, modifier: Modifier) {
    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            ImageView(ctx).apply {
                scaleType = ImageView.ScaleType.FIT_CENTER
                setImageDrawable(drawable)
                drawable.repeatCount = AnimatedImageDrawable.REPEAT_INFINITE
                drawable.start()
            }
        },
    )
}

