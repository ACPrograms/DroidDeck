package com.droiddeck.launcher.gpu

import android.os.Process
import android.util.Log
import com.droiddeck.launcher.core.HostProcess
import com.droiddeck.launcher.core.SessionPart
import java.io.File

class VkBridgeComponent(private val socket: File, private val logFile: File) : SessionPart() {
    @Volatile private var pid = -1
    @Volatile private var exited = false

    override fun start() {
        stop()
        val binary = File(app().applicationInfo.nativeLibraryDir, BINARY)
        if (!binary.isFile) {
            Log.e(TAG, "server missing at $binary; the session will have no Vulkan")
            return
        }
        socket.parentFile?.mkdirs()
        val cacheDir = File(app().cacheDir, "vkbridge-pipelines").apply { mkdirs() }
        if (VkBridgeNative.start(app(), socket, logFile, cacheDir)) {
            Log.i(TAG, "server ready in the app's process at $socket")
            return
        }
        socket.delete()
        exited = false
        val cmd = listOf(binary.absolutePath, "--socket", socket.absolutePath, "--log", logFile.absolutePath,
            "--cache-dir", cacheDir.absolutePath)
            .joinToString(" ") { it.replace("\\", "\\\\").replace(" ", "\\ ") }
        val env = arrayOf("HOME=" + app().filesDir)
        pid = HostProcess.start(cmd, env, app().filesDir, { status ->
            exited = true
            Log.w(TAG, "server exited with status $status")
            runCatching { logFile.appendText("== vkbridge server exited with status $status\n") }
        }, { line -> Log.i(TAG, line) })
        val deadline = System.currentTimeMillis() + 15_000L
        while (!socket.exists() && !exited && System.currentTimeMillis() < deadline) {
            try { Thread.sleep(50L) } catch (e: InterruptedException) { Thread.currentThread().interrupt(); break }
        }
        Log.i(TAG, if (socket.exists()) "server ready (pid $pid) at $socket" else "server NOT ready (pid $pid, exited=$exited); see $logFile")
        if (!socket.exists() && VkBridgeNative.start(app(), socket, logFile, cacheDir)) Log.i(TAG, "server ready in the app's process at $socket")
    }

    override fun stop() {
        if (pid > 0) {
            Process.killProcess(pid)
            pid = -1
        }
        if (!VkBridgeNative.serving) socket.delete()
    }

    override fun suspendPid(): Int = pid

    companion object {
        private const val TAG = "VkBridge"
        const val BINARY = "libvkbridge_server.so"
        const val GUEST_ICD = "/usr/local/share/vkbridge/droidbridge_icd.json"
        const val GUEST_LIB = "/usr/local/lib/vkbridge/libvulkan_droidbridge.so"
    }
}
