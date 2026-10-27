package com.droiddeck.launcher.ui

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlin.js.ExperimentalWasmJsInterop

@OptIn(ExperimentalWasmJsInterop::class)
private fun queryPrefersReducedMotion(): Boolean =
    js("""
        (function() {
            try {
                return Boolean(typeof window !== 'undefined' && window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches);
            } catch (e) {
                return false;
            }
        })()
    """)

internal object Motion {
    var scale: Float by mutableStateOf(if (queryPrefersReducedMotion()) 0f else 1f)

    fun syncReducedMotion() {
        scale = if (queryPrefersReducedMotion()) 0f else 1f
    }
}
