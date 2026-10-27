package com.droiddeck.launcher.ui

import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.Easing
import androidx.compose.animation.core.FiniteAnimationSpec
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.snap
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlin.js.ExperimentalWasmJsInterop
import kotlin.math.roundToInt

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

    val Ease: Easing = CubicBezierEasing(0.2f, 0.8f, 0.2f, 1f)

    fun ms(base: Int): Int = (base * scale).roundToInt()

    fun <T> tw(base: Int, delay: Int = 0, easing: Easing = Ease): FiniteAnimationSpec<T> =
        if (scale == 0f) snap() else tween(base, delay, easing)

    fun <T> sp(damping: Float = 0.7f, stiffness: Float = Spring.StiffnessMediumLow): FiniteAnimationSpec<T> =
        if (scale == 0f) snap() else spring(damping, stiffness)
}
