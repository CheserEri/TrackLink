package com.tracklink.remote

import android.Manifest
import android.accessibilityservice.AccessibilityServiceInfo
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
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import com.tracklink.remote.accessibility.GestureDispatcher
import com.tracklink.remote.accessibility.TrackLinkAccessibilityService
import com.tracklink.remote.bluetooth.BluetoothConnectionManager
import com.tracklink.remote.ui.ConnectionScreen

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
            val state by manager.state.collectAsState()
            MaterialTheme {
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