import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.window.ComposeViewport
import com.droiddeck.launcher.ui.EmptyLibrary
import com.droiddeck.launcher.ui.LocalPalette
import com.droiddeck.launcher.ui.Motion
import com.droiddeck.launcher.ui.Themes
import kotlin.js.ExperimentalWasmJsInterop
import kotlinx.browser.document

// Minimal JS interop to safely notify host WebView2 if available
@OptIn(ExperimentalWasmJsInterop::class)
private fun notifyWebView2Ready() {
    js(
        """
        try {
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(JSON.stringify({ type: "DROIDDECK_UI_READY", status: "ok" }));
            }
        } catch (e) {
            console.log("WebView2 notification skipped:", e);
        }
        """
    )
}

@OptIn(ExperimentalComposeUiApi::class)
fun main() {
    val body = document.body ?: return

    // Ensure motion preference is synced on startup
    Motion.syncReducedMotion()

    ComposeViewport(body) {
        val palette = Themes.graphitePalette
        val colorScheme = darkColorScheme(
            background = palette.background,
            surface = palette.surface,
            onBackground = palette.onBackground,
            onSurface = palette.onBackground,
            primary = palette.primary,
        )

        CompositionLocalProvider(LocalPalette provides palette) {
            MaterialTheme(colorScheme = colorScheme) {
                // Original EmptyLibrary rendered directly from upstream source without extra overlays
                Box(modifier = Modifier.fillMaxSize()) {
                    EmptyLibrary(
                        avoid = null,
                        wall = null,
                        fade = {}
                    )
                }

                // Defer notification until composition and at least two frames have rendered:
                LaunchedEffect(Unit) {
                    withFrameNanos { }
                    withFrameNanos { }
                    notifyWebView2Ready()
                }
            }
        }
    }
}
