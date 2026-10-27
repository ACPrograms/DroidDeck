package com.droiddeck.launcher.ui

import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance

/**
 * Exact palette definition matching original Theme.kt GRAPHITE theme:
 * signal / primary = 0xFF1A9FFF
 * surface = 0xFF121417
 * line = 0xFF262A31
 * background = 0xFF0A0B0D
 * surfaceVariant = 0xFF1A1D22
 * line2 = 0xFF343A43
 * onBackground = 0xFFF2F4F7
 * onSurfaceVariant = 0xFF9AA3AF
 * primary2 = 0xFF1487DB
 * onPrimary = 0xFF03111F
 * good = 0xFF4CD37F
 * error = 0xFFFF8A80
 */
class Palette(
    val id: String,
    val background: Color,
    val surface: Color,
    val surfaceVariant: Color,
    val line: Color,
    val line2: Color,
    val onBackground: Color,
    val onSurfaceVariant: Color,
    val primary: Color,
    val primary2: Color,
    val onPrimary: Color,
    val signal: Color,
    val good: Color,
    val error: Color = Color(0xFFFF8A80),
)

val Palette.onSignal: Color get() = if (signal.luminance() > 0.18f) Color(0xFF03111F) else Color.White

object Themes {
    const val GRAPHITE = "graphite"

    val graphitePalette = Palette(
        id = GRAPHITE,
        background = Color(0xFF0A0B0D),
        surface = Color(0xFF121417),
        surfaceVariant = Color(0xFF1A1D22),
        line = Color(0xFF262A31),
        line2 = Color(0xFF343A43),
        onBackground = Color(0xFFF2F4F7),
        onSurfaceVariant = Color(0xFF9AA3AF),
        primary = Color(0xFF1A9FFF),
        primary2 = Color(0xFF1487DB),
        onPrimary = Color(0xFF03111F),
        signal = Color(0xFF1A9FFF),
        good = Color(0xFF4CD37F),
    )
}

val LocalPalette = staticCompositionLocalOf { Themes.graphitePalette }
