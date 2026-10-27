package androidx.compose.ui.platform

import androidx.compose.runtime.ProvidableCompositionLocal
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.LifecycleObserver
import androidx.lifecycle.LifecycleOwner

class WasmLifecycle : Lifecycle() {
    private val observers = mutableListOf<LifecycleObserver>()

    override val currentState: State
        get() = State.RESUMED

    override fun addObserver(observer: LifecycleObserver) {
        observers.add(observer)
    }

    override fun removeObserver(observer: LifecycleObserver) {
        observers.remove(observer)
    }
}

object WasmLifecycleOwner : LifecycleOwner {
    override val lifecycle: Lifecycle = WasmLifecycle()
}

val LocalLifecycleOwner: ProvidableCompositionLocal<LifecycleOwner> =
    staticCompositionLocalOf { WasmLifecycleOwner }
