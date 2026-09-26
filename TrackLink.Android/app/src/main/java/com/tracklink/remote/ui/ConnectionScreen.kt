package com.tracklink.remote.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.tracklink.remote.accessibility.GestureCommand
import com.tracklink.remote.bluetooth.CommandPayload
import com.tracklink.remote.bluetooth.LinkState
import com.tracklink.remote.bluetooth.LinkUiState
import com.tracklink.remote.bluetooth.PeerDevice

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
    Scaffold { insets ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(insets)
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Text("TrackLink · 蓝牙链路", style = MaterialTheme.typography.headlineSmall)
            Text(
                "服务 UUID ${com.tracklink.remote.bluetooth.BluetoothConnectionManager.SERVICE_UUID_STRING}",
                style = MaterialTheme.typography.bodySmall,
            )

            PermissionCard(permissionsGranted, onRequestPermissions)
            AccessibilityCard(accessibilityEnabled, onOpenAccessibilitySettings)
            LinkStatusCard(state, onDisconnect)
            SelfTestCard(accessibilityEnabled, onSelfTest)

            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = onRefresh, enabled = permissionsGranted) {
                    Text("刷新已配对")
                }
                OutlinedButton(onClick = onScan, enabled = permissionsGranted && !state.scanning) {
                    Text(if (state.scanning) "扫描中…" else "扫描周围设备")
                }
            }

            if (!state.enabled && permissionsGranted) {
                Text(
                    "蓝牙未开启：请在系统设置中打开蓝牙。",
                    color = Color(0xFFB3261E),
                    style = MaterialTheme.typography.bodyMedium,
                )
            }

            Text("设备（${state.peers.size}）", style = MaterialTheme.typography.titleMedium)
            if (state.peers.isEmpty()) {
                Text(
                    "列表为空。请先在系统设置里把手机与笔记本配对，再点“刷新已配对”。",
                    style = MaterialTheme.typography.bodyMedium,
                )
            }

            state.peers.forEach { peer ->
                PeerRow(
                    peer = peer,
                    enabled = state.state == LinkState.IDLE || state.state == LinkState.FAILED,
                    onConnect = { onConnect(peer) },
                )
            }

            HorizontalDivider()
            Text("日志", style = MaterialTheme.typography.titleMedium)
            Text(
                if (state.log.isEmpty()) "（暂无）" else state.log.joinToString("\n"),
                style = MaterialTheme.typography.bodySmall,
                fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace,
            )
            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
private fun PermissionCard(permissionsGranted: Boolean, onRequestPermissions: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = if (permissionsGranted) {
                MaterialTheme.colorScheme.surfaceVariant
            } else {
                Color(0xFFFFE0E0)
            },
        ),
    ) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(
                if (permissionsGranted) "蓝牙权限：已授予" else "蓝牙权限：未授予",
                style = MaterialTheme.typography.titleSmall,
            )
            if (!permissionsGranted) {
                Text(
                    "Android 12 及以上需要“附近的设备”权限才能扫描与连接。",
                    style = MaterialTheme.typography.bodySmall,
                )
                Button(onClick = onRequestPermissions) { Text("申请权限") }
            }
        }
    }
}

@Composable
private fun AccessibilityCard(enabled: Boolean, onOpenSettings: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(
            containerColor = if (enabled) {
                MaterialTheme.colorScheme.surfaceVariant
            } else {
                Color(0xFFFFE0E0)
            },
        ),
    ) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(
                if (enabled) "无障碍服务：已启用" else "无障碍服务：未启用",
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
            Button(onClick = onOpenSettings) { Text("打开无障碍设置") }
        }
    }
}

/** 只在本周用于分层排查：不接蓝牙也能确认手势是否真的生效。 */
@Composable
private fun SelfTestCard(enabled: Boolean, onSelfTest: (GestureCommand) -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("无障碍自测（本地触发，不经蓝牙）", style = MaterialTheme.typography.titleSmall)
            Text(
                "先在这里确认手势真的生效，再排查蓝牙链路。返回与回桌面会切走当前界面。",
                style = MaterialTheme.typography.bodySmall,
            )

            // 屏幕中心：归一化 0..65535 的中点。
            OutlinedButton(
                onClick = { onSelfTest(GestureCommand.Tap(32767, 32767)) },
                enabled = enabled,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("点击屏幕中心") }

            OutlinedButton(
                onClick = { onSelfTest(GestureCommand.Scroll(0, -12000)) },
                enabled = enabled,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("向上滑（内容下移）") }

            OutlinedButton(
                onClick = { onSelfTest(GestureCommand.Global(CommandPayload.GLOBAL_BACK)) },
                enabled = enabled,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("返回上一页") }

            OutlinedButton(
                onClick = { onSelfTest(GestureCommand.Global(CommandPayload.GLOBAL_HOME)) },
                enabled = enabled,
                modifier = Modifier.fillMaxWidth(),
            ) { Text("回到桌面") }

            if (!enabled) {
                Text(
                    "无障碍服务未启用，自测按钮不可用。",
                    color = Color(0xFFB3261E),
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }
}

@Composable
private fun LinkStatusCard(state: LinkUiState, onDisconnect: () -> Unit) {
    val (label, color) = when (state.state) {
        LinkState.IDLE -> "未连接" to MaterialTheme.colorScheme.onSurfaceVariant
        LinkState.CONNECTING -> "连接中…" to Color(0xFF8A6D00)
        LinkState.CONNECTED -> "已连接" to Color(0xFF1B5E20)
        LinkState.FAILED -> "连接失败" to Color(0xFFB3261E)
    }

    Card(modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(label, color = color, style = MaterialTheme.typography.titleMedium)
            state.peer?.let {
                Text("设备：${it.name} (${it.address})", style = MaterialTheme.typography.bodyMedium)
            }
            Text(
                "心跳往返：${state.rttMs?.let { "$it ms" } ?: "—"}",
                style = MaterialTheme.typography.bodyMedium,
            )
            state.error?.let {
                Text("原因：$it", color = Color(0xFFB3261E), style = MaterialTheme.typography.bodySmall)
            }
            if (state.state == LinkState.CONNECTED || state.state == LinkState.CONNECTING) {
                Button(onClick = onDisconnect) { Text("断开") }
            }
        }
    }
}

@Composable
private fun PeerRow(peer: PeerDevice, enabled: Boolean, onConnect: () -> Unit) {
    Card(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(peer.displayName, style = MaterialTheme.typography.bodyLarge)
                Text(peer.address, style = MaterialTheme.typography.bodySmall)
            }
            Button(onClick = onConnect, enabled = enabled) { Text("连接") }
        }
    }
}