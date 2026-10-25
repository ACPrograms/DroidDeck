package com.example.dsedge

import android.app.Application
import android.content.ComponentName
import android.content.pm.PackageManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = Application::class)
class EmbeddedBridgeTest {
    @Test
    fun driverComponentsStayPrivateAndRunOutsideTheSessionProcess() {
        val app = RuntimeEnvironment.getApplication()
        val pm = app.packageManager
        val service = pm.getServiceInfo(ComponentName(app, "com.example.dsedge.BridgeService"), 0)
        assertFalse(service.exported)
        assertEquals("${app.packageName}:sdl3", service.processName)
        for (name in listOf("BridgeActivity", "SdlHostActivity")) {
            val activity = pm.getActivityInfo(ComponentName(app, "com.example.dsedge.$name"), 0)
            assertFalse(activity.exported)
            assertEquals(service.processName, activity.processName)
        }
        val usb = pm.getActivityInfo(ComponentName(app, "com.example.dsedge.UsbAttachActivity"),
            PackageManager.GET_META_DATA)
        assertTrue(usb.exported)
        assertEquals(service.processName, usb.processName)
        assertTrue(usb.metaData.containsKey("android.hardware.usb.action.USB_DEVICE_ATTACHED"))
    }

    @Test
    fun embeddedBridgePreferencesDoNotShareDroidDeckSettings() {
        val app = RuntimeEnvironment.getApplication()
        assertTrue(Host.embedded(app))
        assertEquals("sdl3bridge_settings", Host.prefs(app, "settings"))
        assertEquals("sdl3bridge_engine", Host.prefs(app, "engine"))
    }
}
