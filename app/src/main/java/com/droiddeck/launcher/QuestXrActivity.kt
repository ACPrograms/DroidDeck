package com.droiddeck.launcher

import android.app.NativeActivity
import android.os.Bundle
import android.view.Surface
import android.util.Log
import com.droiddeck.launcher.gpu.SteamVr
import com.droiddeck.launcher.session.SessionService
import com.droiddeck.launcher.wayland.CompositorHost
import com.droiddeck.launcher.wayland.WaylandCompositor

class QuestXrActivity : NativeActivity() {
    private var presentedBaseline = 0L
    private var steamVrStarted = false
    @Volatile private var closing = false
    @Volatile private var steamVrStatus = SteamVr.Status.UNAVAILABLE
    override fun onCreate(savedInstanceState: Bundle?) {
        if (intent.getBooleanExtra("guest_surface", false)) {
            intent.putExtra("surface_test", true)
            SessionService.resume(this)
        }
        super.onCreate(savedInstanceState)
    }

    fun onXrSurface(surface: Surface?, width: Int, height: Int) {
        if (surface != null) {
            presentedBaseline = WaylandCompositor.nativePresentedFrames()
            WaylandCompositor.nativeSetCardboard(false, 1, 0.064f, 3f, 4f, 0f, 0f, 90f, 0f, 0f)
            WaylandCompositor.nativeSetFrameGenArmed(false, 0, 0)
            WaylandCompositor.nativeSetZeroCopy(false)
            WaylandCompositor.nativeSetOutputSize(width, height)
            WaylandCompositor.nativeSetFpsLimit(0)
        }
        CompositorHost.attachXr(surface)
        SessionService.setActivityVisible(this, surface != null)
        WaylandCompositor.nativeVsync(System.nanoTime())
        check(WaylandCompositor.nativeWaitSurface(3000)) { "XR surface GPU handshake timed out" }
        if (surface != null && intent.getBooleanExtra("start_steamvr", false) && !steamVrStarted) {
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

    fun onXrFrame(frameTimeNanos: Long): Long {
        WaylandCompositor.nativeVsync(frameTimeNanos)
        return (WaylandCompositor.nativePresentedFrames() - presentedBaseline).coerceAtLeast(0)
    }

    fun onXrStatus(): Int = steamVrStatus.ordinal

    override fun onDestroy() {
        closing = true
        if (steamVrStarted) Thread({ SteamVr.run(this, "stop") }, "quest-steamvr-stop").start()
        super.onDestroy()
    }
}
