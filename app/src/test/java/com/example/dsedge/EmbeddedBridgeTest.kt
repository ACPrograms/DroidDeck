package com.example.dsedge

import android.app.Application
import android.content.ComponentName
import android.content.ContextWrapper
import android.content.pm.PackageManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Robolectric
import org.robolectric.annotation.Config

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [28], application = Application::class)
class EmbeddedBridgeTest {
    @Test
    fun calibrationUsesOnlyTheSelectedPlayer() {
        Settings.init(RuntimeEnvironment.getApplication())
        val activity = Robolectric.buildActivity(BridgeActivity::class.java).get()
        fun field(name: String) = BridgeActivity::class.java.getDeclaredField(name).apply { isAccessible = true }
        field("calibPlayer").setInt(activity, 2)
        field("calibCount").setInt(activity, 0)
        val frame = FloatArray(13).apply { this[7] = 0.5f; this[8] = -1f; this[9] = 2f }
        activity.onFrame(frame, 1)
        assertEquals(0, field("calibCount").getInt(activity))
        repeat(180) { activity.onFrame(frame, 2) }
        assertEquals(-1, field("calibCount").getInt(activity))
        assertEquals(28.64789f, Settings.gyroBias[2][0], 0.0001f)
        assertEquals(-57.29578f, Settings.gyroBias[2][1], 0.0001f)
        assertEquals(114.59156f, Settings.gyroBias[2][2], 0.0001f)
    }

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

    @Test
    fun headlessEmbeddedDriverLeavesAndroidControllersToDroidDeck() {
        val app = RuntimeEnvironment.getApplication()
        assertTrue(Host.usbOnly(app, false))
        assertTrue(Host.usbOnly(app, true))
        val standalone = object : ContextWrapper(app) {
            override fun getPackageName() = Host.OWN_PACKAGE
        }
        assertFalse(Host.usbOnly(standalone, false))
        assertTrue(Host.usbOnly(standalone, true))
    }
}
