package com.tracklink.remote.ui.theme

import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.material3.Typography

/**
 * 原神 · 经典米黄金色板。
 *
 * 色值与 Windows 端 `UI/Theme/GenshinTheme.xaml` 逐项对应：游戏内 UI 的奶油羊皮纸底 +
 * 金色描边 + 深褐文字。改这里就必须同步改那边，否则两端观感会分叉。
 */
object Genshin {
    /** 页面底色。 */
    val Page = Color(0xFFECE5D8)

    /** 卡片底色。 */
    val Card = Color(0xFFF7F2E7)

    /** 卡片高光（悬停 / 强调时用）。 */
    val CardHi = Color(0xFFFFFBF2)

    /** 金色描边。 */
    val Gold = Color(0xFFD3BC8E)

    /** 深金（描边加重、图标）。 */
    val GoldDeep = Color(0xFFB08D57)

    /** 亮金（渐变起点）。 */
    val GoldHi = Color(0xFFFFE7A8)

    /** 亮金（渐变终点）。 */
    val GoldLow = Color(0xFFE4C67F)

    /** 金色按钮上的深色文字。 */
    val GoldInk = Color(0xFF7A5C1E)

    /** 主文字（深靛灰）。 */
    val TextMain = Color(0xFF3B4255)

    /** 次文字。 */
    val TextSub = Color(0xFF7C7466)

    /** 弱文字。 */
    val TextWeak = Color(0xFFA79E8C)

    /** 成功（草元素绿）。 */
    val Ok = Color(0xFF7BAE3F)

    /** 失败（火元素红）。 */
    val Danger = Color(0xFFC0392B)

    /** 进行中（岩元素金）。 */
    val Warn = Color(0xFFD3A017)

    /** 水蓝。 */
    val Water = Color(0xFF4CC2F1)

    /** 雷紫。 */
    val Violet = Color(0xFFB08FC7)

    /** 未点亮的心跳信号条。 */
    val Track = Color(0xFFDED3BC)

    /** 侧栏 / 顶栏深色渐变。 */
    val SideTop = Color(0xFF3B4255)
    val SideBottom = Color(0xFF4A5468)

    /** 金色渐变（按钮、进度条）。 */
    val GoldGradient = Brush.linearGradient(listOf(GoldHi, GoldLow))

    /** 深色区块渐变。 */
    val SideGradient = Brush.linearGradient(listOf(SideTop, SideBottom))
}

/**
 * 排版：Android 默认字体就行，只调字重与字号，标题加重、说明文字收窄。
 */
val GenshinTypography = Typography().let { base ->
    base.copy(
        headlineSmall = base.headlineSmall.copy(
            fontFamily = FontFamily.SansSerif,
            fontWeight = FontWeight.Bold,
            fontSize = 21.sp,
            color = Genshin.TextMain,
        ),
        titleMedium = base.titleMedium.copy(
            fontWeight = FontWeight.Bold,
            fontSize = 15.sp,
            color = Genshin.TextMain,
        ),
        titleSmall = base.titleSmall.copy(
            fontWeight = FontWeight.Bold,
            fontSize = 14.sp,
            color = Genshin.TextMain,
        ),
        bodyLarge = base.bodyLarge.copy(fontSize = 14.sp, color = Genshin.TextMain),
        bodyMedium = base.bodyMedium.copy(fontSize = 13.sp, color = Genshin.TextMain),
        bodySmall = base.bodySmall.copy(fontSize = 11.5.sp, color = Genshin.TextSub),
    )
}

/** 常用尺寸，避免各处硬编码 dp 不一致。 */
object Dimens {
    val CardCorner = 14.dp
    val CardPadding = 14.dp
    val CardBorder = 1.2.dp
    val ScreenPadding = 16.dp
    val Gap = 12.dp
}