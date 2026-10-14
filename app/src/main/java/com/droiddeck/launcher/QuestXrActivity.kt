package com.droiddeck.launcher

import android.app.NativeActivity
import android.os.Bundle
import android.view.Surface
import com.droiddeck.launcher.session.SessionService
import com.droiddeck.launcher.wayland.CompositorHost
import com.droiddeck.launcher.wayland.WaylandCompositor
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class QuestXrActivity : NativeActivity() {
    private var presentedBaseline = 0L
    override fun onCreate(savedInstanceState: Bundle?) {
        if (intent.getBooleanExtra("guest_surface", false)) {
            require(CompositorHost.isStarted) { "Start a DroidDeck session before attaching XR" }
            intent.putExtra("surface_test", true)
        }
        super.onCreate(savedInstanceState)
    }

    fun onXrSurface(surface: Surface?, width: Int, height: Int) {
        val complete = CountDownLatch(1)
        var failure: Throwable? = null
        runOnUiThread {
            try {
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
            } catch (error: Throwable) {
                failure = error
            } finally {
                complete.countDown()
            }
        }
        check(complete.await(3, TimeUnit.SECONDS)) { "XR surface UI handshake timed out" }
        failure?.let { throw it }
        check(WaylandCompositor.nativeWaitSurface(3000)) { "XR surface GPU handshake timed out" }
    }

    fun onXrFrame(frameTimeNanos: Long): Long {
        WaylandCompositor.nativeVsync(frameTimeNanos)
        return (WaylandCompositor.nativePresentedFrames() - presentedBaseline).coerceAtLeast(0)
    }
}
