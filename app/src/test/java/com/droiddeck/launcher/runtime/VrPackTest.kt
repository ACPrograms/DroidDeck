package com.droiddeck.launcher.runtime

import android.content.Context
import com.droiddeck.launcher.R
import com.droiddeck.launcher.core.FileUtils
import com.droiddeck.launcher.session.SessionPhase
import com.droiddeck.launcher.session.SessionState
import java.io.File
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment

@RunWith(RobolectricTestRunner::class)
class VrPackTest {
    private val context: Context = RuntimeEnvironment.getApplication()
    private val root get() = LinuxRuntime.rootDir(context)
    private val layer = "usr/local/lib/droiddeck-vr/layer/libVkLayer_droiddeck_vr.so"
    private val manifest = "usr/share/vulkan/implicit_layer.d/droiddeck_vr.json"

    @Before fun idle() {
        SessionState.running = false
        SessionState.phase = SessionPhase.IDLE
    }

    @After fun clean() {
        FileUtils.delete(root)
    }

    private fun guest(relative: String) = File(root, relative).apply { parentFile!!.mkdirs(); writeText("old") }

    private fun until(done: (VrPack.Removal) -> Boolean): VrPack.Removal {
        val deadline = System.currentTimeMillis() + 10_000
        while (System.currentTimeMillis() < deadline) {
            val now = VrPack.removal.value
            if (!now.busy && done(now)) return now
            Thread.sleep(20)
        }
        throw AssertionError("uninstall did not finish")
    }

    @Test fun stagingWithoutThePackRemovesWhatAnOlderAppLeft() {
        guest(layer)
        guest(manifest)
        val other = guest("usr/share/vulkan/implicit_layer.d/other.json")
        VrPack.stageGuest(context, root)
        assertFalse(File(root, "usr/local/lib/droiddeck-vr").exists())
        assertFalse(File(root, manifest).exists())
        assertTrue(other.isFile)
    }

    @Test(timeout = 20_000) fun uninstallWaitsForTheSessionThenRemovesTheGuestFiles() {
        guest(layer)
        guest(manifest)
        SessionState.running = true
        VrPack.uninstall(context)
        assertEquals(context.getString(R.string.vr_module_busy), until { it.error != null }.error)
        assertTrue(File(root, layer).isFile)
        SessionState.running = false
        VrPack.uninstall(context)
        until { it.error == null && !File(root, manifest).exists() }
        assertFalse(File(root, "usr/local/lib/droiddeck-vr").exists())
        assertFalse(File(root, manifest).exists())
        assertFalse(VrPack.installed(context))
        assertFalse(LinuxRuntimeInstaller.isBusy())
    }

    @Test fun librariesDoNotLoadWithoutThePack() {
        assertFalse(VrPack.loadNative(context))
    }
}
