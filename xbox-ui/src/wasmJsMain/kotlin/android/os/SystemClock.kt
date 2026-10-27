package android.os

import kotlin.time.TimeSource

object SystemClock {
    private val mark = TimeSource.Monotonic.markNow()

    fun uptimeMillis(): Long = mark.elapsedNow().inWholeMilliseconds
}
