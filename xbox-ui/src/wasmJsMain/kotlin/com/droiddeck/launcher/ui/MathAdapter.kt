package com.droiddeck.launcher.ui

import kotlin.math.PI

object Math {
    const val PI = kotlin.math.PI
    fun toRadians(angdeg: Double): Double = angdeg * PI / 180.0
}
