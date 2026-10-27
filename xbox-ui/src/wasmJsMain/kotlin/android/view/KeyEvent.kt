package android.view

import kotlin.js.ExperimentalWasmJsInterop
import kotlin.js.JsAny
import androidx.compose.ui.input.key.Key
import androidx.compose.ui.input.key.KeyEvent as ComposeKeyEvent
import androidx.compose.ui.input.key.KeyEventType
import androidx.compose.ui.input.key.key
import androidx.compose.ui.input.key.type

class KeyEvent {
    companion object {
        const val ACTION_DOWN = 0
        const val ACTION_UP = 1

        const val KEYCODE_DPAD_UP = 19
        const val KEYCODE_DPAD_DOWN = 20
        const val KEYCODE_DPAD_LEFT = 21
        const val KEYCODE_DPAD_RIGHT = 22
        const val KEYCODE_BUTTON_A = 96
        const val KEYCODE_BUTTON_B = 97
        const val KEYCODE_BUTTON_X = 99
        const val KEYCODE_BUTTON_Y = 100
        const val KEYCODE_BUTTON_L1 = 102
        const val KEYCODE_BUTTON_R1 = 103
        const val KEYCODE_BACK = 4
    }
}

@OptIn(ExperimentalWasmJsInterop::class)
fun extractKeyCode(target: JsAny?): Int = js(
    """
    (function(e) {
        if (!e) return 0;
        var ev = e.nativeEvent || e;
        var key = ev.key || '';
        if (key === 'ArrowUp') return 19;
        if (key === 'ArrowDown') return 20;
        if (key === 'ArrowLeft') return 21;
        if (key === 'ArrowRight') return 22;
        if (key === 'Enter' || key === ' ') return 96;
        if (key === 'Escape' || key === 'Backspace') return 97;
        if (key === '[' || key === 'PageUp') return 102;
        if (key === ']' || key === 'PageDown') return 103;
        if (typeof ev.keyCode === 'number') return ev.keyCode;
        return 0;
    })(target)
    """
)

@OptIn(ExperimentalWasmJsInterop::class)
fun extractAction(target: JsAny?): Int = js(
    """
    (function(e) {
        if (!e) return 0;
        var ev = e.nativeEvent || e;
        var type = ev.type || '';
        return type === 'keyup' ? 1 : 0;
    })(target)
    """
)

@OptIn(ExperimentalWasmJsInterop::class)
fun extractRepeatCount(target: JsAny?): Int = js(
    """
    (function(e) {
        if (!e) return 0;
        var ev = e.nativeEvent || e;
        return ev.repeat ? 1 : 0;
    })(target)
    """
)

// Compose 1.9.3 wraps the DOM event in an InternalKeyEvent Kotlin object.
// Re-wrap it through the public Compose API instead of casting that object to JsAny.
private fun Any.composeEvent(): ComposeKeyEvent? = runCatching {
    ComposeKeyEvent(this).also { it.type }
}.getOrNull()

val Any.keyCode: Int
    get() {
        val event = composeEvent() ?: return extractKeyCode(this as? JsAny)
        return when (event.key) {
            Key.DirectionUp -> KeyEvent.KEYCODE_DPAD_UP
            Key.DirectionDown -> KeyEvent.KEYCODE_DPAD_DOWN
            Key.DirectionLeft -> KeyEvent.KEYCODE_DPAD_LEFT
            Key.DirectionRight -> KeyEvent.KEYCODE_DPAD_RIGHT
            Key.Enter, Key.NumPadEnter, Key.Spacebar -> KeyEvent.KEYCODE_BUTTON_A
            Key.Escape, Key.Backspace -> KeyEvent.KEYCODE_BUTTON_B
            Key.X -> KeyEvent.KEYCODE_BUTTON_X
            Key.Y -> KeyEvent.KEYCODE_BUTTON_Y
            Key.PageUp, Key.LeftBracket -> KeyEvent.KEYCODE_BUTTON_L1
            Key.PageDown, Key.RightBracket -> KeyEvent.KEYCODE_BUTTON_R1
            else -> event.key.keyCode.toInt()
        }
    }

val Any.action: Int
    get() = composeEvent()?.let { if (it.type == KeyEventType.KeyUp) KeyEvent.ACTION_UP else KeyEvent.ACTION_DOWN }
        ?: extractAction(this as? JsAny)

val Any.repeatCount: Int
    get() = extractRepeatCount(this as? JsAny)
