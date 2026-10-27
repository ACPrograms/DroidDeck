import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.window.ComposeViewport
import com.droiddeck.launcher.ui.FrontEndActions
import com.droiddeck.launcher.ui.FrontEndState
import com.droiddeck.launcher.ui.FrontFocus
import com.droiddeck.launcher.ui.LocalFrontFocus
import com.droiddeck.launcher.ui.LocalPalette
import com.droiddeck.launcher.ui.Motion
import com.droiddeck.launcher.ui.SteamHome
import com.droiddeck.launcher.ui.Themes
import kotlin.js.ExperimentalWasmJsInterop
import kotlinx.browser.document
import kotlinx.browser.window
import org.w3c.dom.events.EventTarget

@OptIn(ExperimentalWasmJsInterop::class)
private fun postHostMessage(type: String) {
    js(
        """
        try {
            var payload = JSON.stringify({ type: type });
            if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(payload);
            }
            if (window.parent && window.parent !== window) {
                window.parent.postMessage(payload, "*");
            }
        } catch (e) {
            console.log("WebView2 notification skipped:", e);
        }
        """
    )
}

@OptIn(ExperimentalWasmJsInterop::class)
private fun getChromeWebView(): EventTarget? =
    js(
        """
        (function() {
            try {
                if (window.chrome && window.chrome.webview && typeof window.chrome.webview.addEventListener === 'function') {
                    return window.chrome.webview;
                }
            } catch (e) {}
            return null;
        })()
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun extractEventData(event: org.w3c.dom.events.Event): JsAny? =
    js(
        """
        (function(e) {
            try {
                if (e && e.data !== undefined && e.data !== null) {
                    return e.data;
                }
            } catch (err) {}
            return null;
        })(event)
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun isStatusChannel(data: JsAny?): Boolean =
    js(
        """
        (function(d) {
            try {
                var obj = d;
                if (typeof d === 'string') {
                    if (d.length > 4096) return false;
                    obj = JSON.parse(d);
                }
                return (obj && typeof obj === 'object' && obj.type === 'DROIDDECK_STATUS');
            } catch (e) {}
            return false;
        })(data)
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun extractBoundedBool(data: JsAny?, key: String): Boolean =
    js(
        """
        (function(d, k) {
            try {
                var obj = d;
                if (typeof d === 'string') { obj = JSON.parse(d); }
                if (obj && obj.type === 'DROIDDECK_STATUS' && typeof obj[k] === 'boolean') {
                    return obj[k];
                }
            } catch (e) {}
            return false;
        })(data, key)
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun extractBoundedStage(data: JsAny?): String =
    js(
        """
        (function(d) {
            try {
                var obj = d;
                if (typeof d === 'string') { obj = JSON.parse(d); }
                if (obj && obj.type === 'DROIDDECK_STATUS' && typeof obj.stage === 'string') {
                    return obj.stage.replace(/[^\x20-\x7E]/g, '').slice(0, 64);
                }
            } catch (e) {}
            return "";
        })(data)
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun extractBoundedPercent(data: JsAny?): Int =
    js(
        """
        (function(d) {
            try {
                var obj = d;
                if (typeof d === 'string') { obj = JSON.parse(d); }
                if (obj && obj.type === 'DROIDDECK_STATUS' && typeof obj.percent === 'number' && isFinite(obj.percent)) {
                    var p = Math.floor(obj.percent);
                    return (p >= 0 && p <= 100) ? p : -1;
                }
            } catch (e) {}
            return -1;
        })(data)
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun extractBoundedVersion(data: JsAny?, key: String): String? =
    js(
        """
        (function(d, k) {
            try {
                var obj = d;
                if (typeof d === 'string') { obj = JSON.parse(d); }
                if (obj && obj.type === 'DROIDDECK_STATUS' && typeof obj[k] === 'string') {
                    var s = obj[k].replace(/[^\x20-\x7E]/g, '').slice(0, 32);
                    return s.length > 0 ? s : null;
                }
            } catch (e) {}
            return null;
        })(data, key)
        """
    )

@OptIn(ExperimentalComposeUiApi::class, ExperimentalWasmJsInterop::class)
fun main() {
    val body = document.body ?: return

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

        var ready by remember { mutableStateOf(false) }
        var busy by remember { mutableStateOf(false) }
        var stage by remember { mutableStateOf("") }
        var percent by remember { mutableIntStateOf(-1) }
        var installed by remember { mutableStateOf<String?>(null) }
        var available by remember { mutableStateOf<String?>(null) }

        val frontFocus = remember { FrontFocus() }

        DisposableEffect(Unit) {
            val listener: (org.w3c.dom.events.Event) -> Unit = { event ->
                val data = extractEventData(event)
                if (data != null && isStatusChannel(data)) {
                    ready = extractBoundedBool(data, "ready")
                    busy = extractBoundedBool(data, "busy")
                    stage = extractBoundedStage(data)
                    percent = extractBoundedPercent(data)
                    installed = extractBoundedVersion(data, "installed")
                    available = extractBoundedVersion(data, "available")
                }
            }

            val webview = getChromeWebView()
            webview?.addEventListener("message", listener)
            window.addEventListener("message", listener)

            onDispose {
                webview?.removeEventListener("message", listener)
                window.removeEventListener("message", listener)
            }
        }

        val state = remember(ready, busy, stage, percent, installed, available) {
            FrontEndState(
                installed = installed,
                ready = ready,
                available = available,
                busy = busy,
                stage = stage,
                percent = percent,
                desktopInstalled = false,
                offlineAccount = null,
                offline = false,
                steamGames = emptyList(),
            )
        }

        val actions = remember {
            FrontEndActions(
                onPlay = {
                    postHostMessage("DROIDDECK_PLAY_STEAM")
                },
                onSteamSettings = {},
            )
        }

        CompositionLocalProvider(
            LocalPalette provides palette,
            LocalFrontFocus provides frontFocus,
        ) {
            MaterialTheme(colorScheme = colorScheme) {
                Box(modifier = Modifier.fillMaxSize().background(palette.background)) {
                    SteamHome(
                        s = state,
                        a = actions,
                        modifier = Modifier.fillMaxSize(),
                    )
                }

                LaunchedEffect(Unit) {
                    withFrameNanos { }
                    withFrameNanos { }
                    runCatching { frontFocus.primary.requestFocus() }
                    postHostMessage("DROIDDECK_UI_READY")
                }
            }
        }
    }
}
