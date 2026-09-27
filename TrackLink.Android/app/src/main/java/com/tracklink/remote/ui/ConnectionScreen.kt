package com.tracklink.remote.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.tracklink.remote.accessibility.GestureCommand
import com.tracklink.remote.bluetooth.BluetoothConnectionManager
import com.tracklink.remote.bluetooth.CommandPayload
import com.tracklink.remote.bluetooth.LinkState
import com.tracklink.remote.bluetooth.LinkUiState
import com.tracklink.remote.bluetooth.PeerDevice
import com.tracklink.remote.ui.theme.Dimens
import com.tracklink.remote.ui.theme.Genshin

@Composable
fun ConnectionScreen(
    state: LinkUiState,
    permissionsGranted: Boolean,
    accessibilityEnabled: Boolean,
    onRequestPermissions: () -> Unit,
    onOpenAccessibilitySettings: () -> Unit,
    onSelfTest: (GestureCommand) -> Unit,
    onRefresh: () -> Unit,
    onScan: () -> Unit,
    onConnect: (PeerDevice) -> Unit,
    onDisconnect: () -> Unit,
) {
    Scaffold(containerColor = Genshin.Page) { insets ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(insets)
                .verticalScroll(rememberScrollState()),
        ) {
            HeroHeader()

            Column(
                modifier = Modifier.padding(Dimens.ScreenPadding),
                verticalArrangement = Arrangement.spacedBy(Dimens.Gap),
            ) {
                LinkStatusCard(state, onDisconnect)
                PermissionCard(permissionsGranted, onRequestPermissions)
                AccessibilityCard(accessibilityEnabled, onOpenAccessibilitySettings)
                DeviceSection(
                    state = state,
                    permissionsGranted = permissionsGranted,
                    onRefresh = onRefresh,
                    onScan = onScan,
                    onConnect = onConnect,
                )
                SelfTestCard(accessibilityEnabled, onSelfTest)
                LogSection(state)
                Spacer(Modifier.height(24.dp))
            }
        }
    }
}

/** 顶部深色渐变横幅：品牌图 + 标题 + 一张欢迎动图。 */
@Composable
private fun HeroHeader() {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(bottomStart = 20.dp, bottomEnd = 20.dp))
            .background(Genshin.SideGradient)
            .padding(horizontal = 18.dp, vertical = 16.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(
                    "TrackLink",
                    color = Genshin.GoldHi,
                    fontWeight = FontWeight.Bold,
                    style = MaterialTheme.typography.headlineSmall,
                )
                Text(
                    "触控板遥控 · Android 端",
                    color = Genshin.TextWeak,
                    style = MaterialTheme.typography.bodySmall,
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    "服务 UUID ${BluetoothConnectionManager.SERVICE_UUID_STRING.take(8)}…",
                    color = Genshin.TextWeak,
                    style = MaterialTheme.typography.bodySmall,
                )
            }

            // 启动欢迎（派蒙开心，动图）：只在顶部装饰，不参与状态表达。
            Sticker(key = StickerKey.welcome, size = 76.dp)
        }
    }
}

/**
 * 链路状态卡：左侧文字信息 + 右侧一张大表情，表情与 [LinkState] 一一对应。
 *
 * 心跳超时（[LinkUiState.timedOut]）必须是独立一格视觉：它和「配对/通道失败」在
 * 原来的实现里都是「连接失败」四个字，用户完全分不出是笔记本没回应还是根本没连上。
 */
@Composable
private fun LinkStatusCard(state: LinkUiState, onDisconnect: () -> Unit) {
    val (label, color) = when (state.state) {
        LinkState.IDLE -> "未连接" to Genshin.TextSub
        LinkState.CONNECTING -> "连接中…" to Genshin.Warn
        LinkState.CONNECTED -> "已连接" to Genshin.Ok
        LinkState.FAILED -> if (state.timedOut) "心跳超时" to Genshin.Danger else "连接失败" to Genshin.Danger
    }

    val sticker = when {
        state.state == LinkState.CONNECTING -> StickerKey.connecting
        state.state == LinkState.CONNECTED -> StickerKey.connected
        state.state == LinkState.FAILED && state.timedOut -> StickerKey.timeout
        state.state == LinkState.FAILED -> StickerKey.lost
        state.scanning -> StickerKey.searching
        else -> StickerKey.waiting
    }

    GenshinCard(accent = color.copy(alpha = 0.55f)) {
        Row(verticalAlignment = Alignment.Top) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                Text(label, color = color, style = MaterialTheme.typography.titleMedium)

                state.peer?.let {
                    FieldText("设备", "${it.name} (${it.address})")
                }

                Row(verticalAlignment = Alignment.CenterVertically) {
                    FieldText("心跳往返", state.rttMs?.let { "$it ms" } ?: "—")
                    // 连上也收到 PONG 了才配得上这张「手势识别正常」。
                    if (state.state == LinkState.CONNECTED && state.rttMs != null) {
                        Spacer(Modifier.width(6.dp))
                        Sticker(key = StickerKey.great, size = 30.dp)
                    }
                }

                HeartbeatBars(level = heartbeatLevel(state))

                state.error?.let {
                    Text("原因：$it", color = Genshin.Danger, style = MaterialTheme.typography.bodySmall)
                }

                if (state.state == LinkState.CONNECTED || state.state == LinkState.CONNECTING) {
                    Spacer(Modifier.height(4.dp))
                    GoldButton(text = "断开", onClick = onDisconnect)
                }
            }

            Spacer(Modifier.width(10.dp))
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Sticker(key = sticker, size = 104.dp)
                Text(
                    sticker.caption,
                    color = Genshin.GoldDeep,
                    fontWeight = FontWeight.SemiBold,
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }
}

/**
 * 四格心跳信号条：按最近一次 RTT 点亮，未连接或还没收到 PONG 时全部熄灭。
 * 与 Windows 端 ConnectionView 的判定阈值保持一致。
 */
@Composable
private fun HeartbeatBars(level: Int) {
    Row(horizontalArrangement = Arrangement.spacedBy(3.dp), verticalAlignment = Alignment.Bottom) {
        listOf(8.dp, 12.dp, 16.dp, 20.dp).forEachIndexed { index, barHeight ->
            Box(
                Modifier
                    .width(5.dp)
                    .height(barHeight)
                    .clip(RoundedCornerShape(3.dp))
                    .background(if (index < level) (if (level >= 3) Genshin.Ok else Genshin.Warn) else Genshin.Track),
            )
        }
    }
}

private fun heartbeatLevel(state: LinkUiState): Int {
    val rtt = state.rttMs
    if (state.state != LinkState.CONNECTED || rtt == null) {
        return 0
    }

    return when {
        rtt <= 25 -> 4
        rtt <= 60 -> 3
        rtt <= 150 -> 2
        else -> 1
    }
}

@Composable
private fun PermissionCard(granted: Boolean, onRequestPermissions: () -> Unit) {
    if (granted) {
        // 已授予就收成一行，别让已经没问题的事占掉一整张卡。
        GenshinCard {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("蓝牙权限", style = MaterialTheme.typography.titleSmall)
                Spacer(Modifier.weight(1f))
                Text("已授予", color = Genshin.Ok, style = MaterialTheme.typography.bodySmall)
            }
        }
        return
    }

    GenshinCard(accent = Genshin.Danger.copy(alpha = 0.6f)) {
        Row(verticalAlignment = Alignment.Top) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                Text("蓝牙权限：未授予", color = Genshin.Danger, style = MaterialTheme.typography.titleSmall)
                Text(
                    "Android 12 及以上需要「附近的设备」权限才能扫描与连接；更早版本需要定位权限才能发现设备。",
                    style = MaterialTheme.typography.bodySmall,
                )
                GoldButton(text = "申请权限", onClick = onRequestPermissions)
            }
            Spacer(Modifier.width(10.dp))
            Sticker(key = StickerKey.rejected, size = 76.dp)
        }
    }
}

@Composable
private fun AccessibilityCard(enabled: Boolean, onOpenSettings: () -> Unit) {
    val state = accessibilityEnabledState(enabled)

    GenshinCard(accent = if (enabled) Genshin.Gold else Genshin.Danger.copy(alpha = 0.6f)) {
        Row(verticalAlignment = Alignment.Top) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                Text(
                    if (enabled) "无障碍服务：已启用" else "无障碍服务：未启用",
                    color = if (enabled) Genshin.Ok else Genshin.Danger,
                    style = MaterialTheme.typography.titleSmall,
                )
                Text(
                    if (enabled) {
                        "可以接收并执行点击、滚动、返回等操作。"
                    } else {
                        "必须在系统设置里手动启用「TrackLink 触控板遥控」，否则收到命令也无法执行。"
                    },
                    style = MaterialTheme.typography.bodySmall,
                )
                if (!enabled) {
                    GoldButton(text = "打开无障碍设置", onClick = onOpenSettings)
                }
            }
            if (state != null) {
                Spacer(Modifier.width(10.dp))
                Sticker(key = state, size = 76.dp)
            }
        }
    }
}

/** 已启用不摆表情：这里只在「被系统挡住」时才需要一张拒绝脸。 */
private fun accessibilityEnabledState(enabled: Boolean): StickerKey? =
    if (enabled) null else StickerKey.rejected

/** 设备区：列表头部的表情跟随扫描 / 空列表 / 有设备三种情况。 */
@Composable
private fun DeviceSection(
    state: LinkUiState,
    permissionsGranted: Boolean,
    onRefresh: () -> Unit,
    onScan: () -> Unit,
    onConnect: (PeerDevice) -> Unit,
) {
    val headerSticker = when {
        state.scanning -> StickerKey.searching
        state.peers.isEmpty() -> StickerKey.nothing
        else -> StickerKey.found
    }

    Row(verticalAlignment = Alignment.CenterVertically) {
        Text("设备（${state.peers.size}）", style = MaterialTheme.typography.titleMedium)
        Spacer(Modifier.width(8.dp))
        Sticker(key = headerSticker, size = 34.dp)
        Spacer(Modifier.width(6.dp))
        Text(headerSticker.caption, style = MaterialTheme.typography.bodySmall)
    }

    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        GoldButton(text = "刷新已配对", onClick = onRefresh, enabled = permissionsGranted)
        GoldButton(
            text = if (state.scanning) "扫描中…" else "扫描周围设备",
            onClick = onScan,
            enabled = permissionsGranted && !state.scanning,
            outlined = true,
        )
    }

    if (!state.enabled && permissionsGranted) {
        GenshinCard(accent = Genshin.Danger.copy(alpha = 0.6f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Sticker(key = StickerKey.shock, size = 54.dp)
                Spacer(Modifier.width(10.dp))
                Text("蓝牙未开启：请在系统设置中打开蓝牙。", color = Genshin.Danger,
                    style = MaterialTheme.typography.bodyMedium)
            }
        }
    }

    if (state.peers.isEmpty()) {
        GenshinCard {
            Text(
                "列表为空。请先在系统设置里把手机与笔记本配对，再点「刷新已配对」。",
                style = MaterialTheme.typography.bodySmall,
            )
        }
        return
    }

    state.peers.forEach { peer ->
        PeerRow(
            peer = peer,
            enabled = state.state == LinkState.IDLE || state.state == LinkState.FAILED,
            onConnect = { onConnect(peer) },
        )
    }
}

/** 只在本周用于分层排查：不接蓝牙也能确认手势是否真的生效。 */
@Composable
private fun SelfTestCard(enabled: Boolean, onSelfTest: (GestureCommand) -> Unit) {
    GenshinCard {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("无障碍自测", style = MaterialTheme.typography.titleSmall)
            if (!enabled) {
                Spacer(Modifier.width(8.dp))
                Sticker(key = StickerKey.hint, size = 34.dp)
            }
        }
        Text(
            "本地触发、不经蓝牙：先在这里确认手势真的生效，再排查蓝牙链路。返回与回桌面会切走当前界面。",
            style = MaterialTheme.typography.bodySmall,
        )

        // 屏幕中心：归一化 0..65535 的中点。
        SelfTestButton("点击屏幕中心", enabled) { onSelfTest(GestureCommand.Tap(32767, 32767)) }
        SelfTestButton("向上滑（内容下移）", enabled) { onSelfTest(GestureCommand.Scroll(0, -12000)) }
        SelfTestButton("返回上一页", enabled) {
            onSelfTest(GestureCommand.Global(CommandPayload.GLOBAL_BACK))
        }
        SelfTestButton("回到桌面", enabled) {
            onSelfTest(GestureCommand.Global(CommandPayload.GLOBAL_HOME))
        }

        if (!enabled) {
            Text("无障碍服务未启用，自测按钮不可用。", color = Genshin.Danger,
                style = MaterialTheme.typography.bodySmall)
        }
    }
}

@Composable
private fun SelfTestButton(text: String, enabled: Boolean, onClick: () -> Unit) {
    GoldButton(text = text, onClick = onClick, enabled = enabled, outlined = true,
        modifier = Modifier.fillMaxWidth())
}

@Composable
private fun LogSection(state: LinkUiState) {
    Text("日志", style = MaterialTheme.typography.titleMedium)
    GenshinCard {
        if (state.log.isEmpty()) {
            Text(
                "（暂无）",
                style = MaterialTheme.typography.bodySmall,
                fontFamily = FontFamily.Monospace,
            )
        } else {
            // 日志会随使用一直累积，必须给固定高度 + 内部滚动：
            // 直接把整份日志铺进卡片的话，卡片会越长越高，把整个页面顶得极长。
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(220.dp)
                    .verticalScroll(rememberScrollState()),
            ) {
                Text(
                    state.log.joinToString("\n"),
                    style = MaterialTheme.typography.bodySmall,
                    fontFamily = FontFamily.Monospace,
                )
            }
        }
    }
}

@Composable
private fun PeerRow(peer: PeerDevice, enabled: Boolean, onConnect: () -> Unit) {
    GenshinCard {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(peer.displayName, style = MaterialTheme.typography.bodyLarge)
                Text(peer.address, style = MaterialTheme.typography.bodySmall)
            }
            GoldButton(text = "连接", onClick = onConnect, enabled = enabled)
        }
    }
}

/** 「标签：值」的一行，标签用次文字色，两者基线对齐。 */
@Composable
private fun FieldText(label: String, value: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text("$label ", color = Genshin.TextSub, style = MaterialTheme.typography.bodySmall)
        Text(value, style = MaterialTheme.typography.bodyMedium)
    }
}

/** 统一的原神风卡片：羊皮纸底 + 金色细描边 + 大圆角，不用阴影（贴合游戏内 UI 的扁平描边感）。 */
@Composable
private fun GenshinCard(
    modifier: Modifier = Modifier,
    accent: Color = Genshin.Gold,
    content: @Composable ColumnScope.() -> Unit,
) {
    Card(
        modifier = modifier.fillMaxWidth(),
        shape = RoundedCornerShape(Dimens.CardCorner),
        colors = CardDefaults.cardColors(containerColor = Genshin.Card),
        border = BorderStroke(Dimens.CardBorder, accent),
        elevation = CardDefaults.cardElevation(defaultElevation = 0.dp),
    ) {
        Column(
            modifier = Modifier.padding(Dimens.CardPadding),
            verticalArrangement = Arrangement.spacedBy(6.dp),
            content = content,
        )
    }
}

/** 金色按钮：亮金渐变 + 深色文字，与原神里确认键的观感一致。 */
@Composable
private fun GoldButton(
    text: String,
    onClick: () -> Unit,
    enabled: Boolean = true,
    outlined: Boolean = false,
    modifier: Modifier = Modifier,
) {
    if (outlined) {
        OutlinedButton(
            onClick = onClick,
            enabled = enabled,
            shape = RoundedCornerShape(10.dp),
            border = BorderStroke(1.dp, if (enabled) Genshin.GoldDeep else Genshin.Track),
            colors = ButtonDefaults.outlinedButtonColors(
                contentColor = Genshin.GoldInk,
                disabledContentColor = Genshin.TextWeak,
                containerColor = Genshin.CardHi,
            ),
            modifier = modifier,
        ) { Text(text) }
        return
    }

    Button(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(10.dp),
        colors = ButtonDefaults.buttonColors(
            containerColor = Genshin.GoldLow,
            contentColor = Genshin.GoldInk,
            disabledContainerColor = Genshin.Track,
            disabledContentColor = Genshin.TextWeak,
        ),
        modifier = modifier,
    ) { Text(text) }
}