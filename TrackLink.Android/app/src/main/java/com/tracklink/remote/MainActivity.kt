package com.tracklink.remote

import android.Manifest
import android.accessibilityservice.AccessibilityServiceInfo
import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.view.accessibility.AccessibilityManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.lifecycle.lifecycleScope
import com.tracklink.remote.accessibility.GestureDispatcher
import com.tracklink.remote.accessibility.TrackLinkAccessibilityService
import com.tracklink.remote.bluetooth.BluetoothConnectionManager
import com.tracklink.remote.ui.ConnectionScreen
import com.tracklink.remote.ui.theme.Genshin
import com.tracklink.remote.ui.theme.GenshinTypography

class MainActivity : ComponentActivity() {

    private lateinit var manager: BluetoothConnectionManager

    private var permissionsGranted by mutableStateOf(false)

    private var accessibilityEnabled by mutableStateOf(false)

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions(),
    ) {
        syncPermissionState()
        if (permissionsGranted) {
            manager.refresh()
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        manager = BluetoothConnectionManager(applicationContext, lifecycleScope)

        // 手势派发日志与蓝牙日志汇到同一条流，方便按时间顺序对齐排查。
        GestureDispatcher.log = { manager.appendLog(it) }
        manager.commandHandler = { GestureDispatcher.submit(it) }

        syncPermissionState()
        accessibilityEnabled = isAccessibilityServiceEnabled()

        setContent {
            TrackLinkTheme {
                val state by manager.state.collectAsState()
                ConnectionScreen(
                    state = state,
                    permissionsGranted = permissionsGranted,
                    accessibilityEnabled = accessibilityEnabled,
                    onRequestPermissions = { permissionLauncher.launch(requiredPermissions().toTypedArray()) },
                    onOpenAccessibilitySettings = { openAccessibilitySettings() },
                    onSelfTest = { GestureDispatcher.submit(it) },
                    onRefresh = { manager.refresh() },
                    onScan = { manager.startDiscovery() },
                    onConnect = { manager.connect(it) },
                    onDisconnect = { manager.disconnect() },
                )
            }
        }
    }

    override fun onResume() {
        super.onResume()
        syncPermissionState()

        // 用户可能刚从系统设置里开/关无障碍服务，回来必须重新判定。
        accessibilityEnabled = isAccessibilityServiceEnabled()

        if (permissionsGranted && !manager.state.value.enabled) {
            manager.refresh()
        }
    }

    override fun onDestroy() {
        GestureDispatcher.log = null
        manager.commandHandler = null
        manager.stopDiscovery()
        manager.disconnect()
        super.onDestroy()
    }

    /** Android 12 起用 BLUETOOTH_SCAN/CONNECT；更早版本发现设备依赖定位权限。 */
    private fun requiredPermissions(): List<String> =
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            listOf(Manifest.permission.BLUETOOTH_SCAN, Manifest.permission.BLUETOOTH_CONNECT)
        } else {
            listOf(Manifest.permission.ACCESS_FINE_LOCATION)
        }

    private fun syncPermissionState() {
        permissionsGranted = requiredPermissions().all {
            ContextCompat.checkSelfPermission(this, it) == PackageManager.PERMISSION_GRANTED
        }
    }

    /**
     * 用系统的「已启用无障碍服务」列表判断，而不是只看服务实例是否连上：
     * 这样用户关掉服务后界面能立刻反映出来，无需等服务被销毁。
     */
    private fun isAccessibilityServiceEnabled(): Boolean {
        val accessibilityManager = getSystemService(AccessibilityManager::class.java) ?: return false
        val expected = ComponentName(this, TrackLinkAccessibilityService::class.java)

        return accessibilityManager
            .getEnabledAccessibilityServiceList(AccessibilityServiceInfo.FEEDBACK_ALL_MASK)
            .any { info ->
                val serviceInfo = info.resolveInfo?.serviceInfo ?: return@any false
                ComponentName(serviceInfo.packageName, serviceInfo.name) == expected
            }
    }

    private fun openAccessibilitySettings() {
        val intent = Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)
        runCatching { startActivity(intent) }
            .onFailure { manager.appendLog("无法打开无障碍设置：${it.message ?: it.javaClass.simpleName}") }
    }
}

/**
 * 原神主题：只有一套浅色米黄金配色，不跟随系统深色——
 * 游戏内那种奶油羊皮纸 + 金色描边的观感在深色底上会完全走样。
 */
@Composable
private fun TrackLinkTheme(content: @Composable () -> Unit) {
    val context = LocalContext.current

    // 状态栏当作顶部深色横幅的延伸（浅色图标），导航栏用页面底色（深色图标）。
    SideEffect {
        val window = (context as? Activity)?.window ?: return@SideEffect
        window.statusBarColor = Genshin.SideTop.toArgb()
        window.navigationBarColor = Genshin.Page.toArgb()
        WindowCompat.getInsetsController(window, window.decorView).apply {
            isAppearanceLightStatusBars = false
            isAppearanceLightNavigationBars = true
        }
    }

    MaterialTheme(
        colorScheme = lightColorScheme(
            primary = Genshin.GoldDeep,
            onPrimary = Genshin.CardHi,
            background = Genshin.Page,
            onBackground = Genshin.TextMain,
            surface = Genshin.Card,
            onSurface = Genshin.TextMain,
            error = Genshin.Danger,
        ),
        typography = GenshinTypography,
        content = content,
    )
}