package com.droiddeck.launcher

import android.app.NativeActivity
import android.os.Bundle
import android.util.Log
import com.droiddeck.launcher.gpu.SteamVr
import com.droiddeck.launcher.session.SessionService
import com.droiddeck.launcher.wayland.CompositorHost
import com.droiddeck.launcher.wayland.WaylandCompositor

class QuestXrActivity : NativeActivity() {
    private var steamVrStarted = false
    @Volatile private var closing = false
    @Volatile private var steamVrStatus = SteamVr.Status.UNAVAILABLE
    override fun onCreate(savedInstanceState: Bundle?) {
        SessionService.setXrActivityVisible(this, true)
        super.onCreate(savedInstanceState)
    }

    fun onXrActive(active: Boolean) {
        if (active) {
            check(CompositorHost.isStarted) { "DroidDeck compositor is not running" }
            SessionService.resume(this)
            WaylandCompositor.nativeSetCardboard(false, 1, 0.064f, 3f, 4f, 0f, 0f, 90f, 0f, 0f)
            WaylandCompositor.nativeSetFrameGenArmed(false, 0, 0)
            WaylandCompositor.nativeSetZeroCopy(false)
            CompositorHost.attachXr()
            WaylandCompositor.nativeVsync(System.nanoTime())
            check(WaylandCompositor.nativeWaitSurface(3000)) { "XR frame GPU handshake timed out" }
        }
        SessionService.setXrActivityVisible(this, active)
        if (active) startSteamVr()
    }

    private fun startSteamVr() {
        if (!steamVrStarted) {
            steamVrStarted = true
            Thread({
                if (!SteamVr.run(this, "start")) Log.e("DroidDeckXR", "SteamVR start failed")
                while (!closing) {
                    steamVrStatus = SteamVr.status(this)
                    Thread.sleep(1000)
                }
                SteamVr.run(this, "stop")
            }, "quest-steamvr-start").start()
        }
    }

    fun onXrFrame(frameTimeNanos: Long) {
        WaylandCompositor.nativeVsync(frameTimeNanos)
    }

    fun onXrStatus(): Int = steamVrStatus.ordinal

    override fun onDestroy() {
        closing = true
        SessionService.setXrActivityVisible(this, false)
        super.onDestroy()
    }

    override fun onStop() {
        SessionService.setXrActivityVisible(this, false)
        super.onStop()
    }
}
