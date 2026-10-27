import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.CompositionLocalProvider
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
import org.w3c.dom.MessageEvent

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
private fun extractStatusString(data: JsAny?, key: String): String? =
    js(
        """
        (function(d, k) {
            try {
                var obj = d;
                if (typeof d === 'string') { obj = JSON.parse(d); }
                if (obj && obj.type === 'DROIDDECK_STATUS' && obj[k] !== undefined && obj[k] !== null) {
                    return String(obj[k]);
                }
            } catch (e) {}
            return null;
        })(data, key)
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun extractStatusBool(data: JsAny?, key: String): Boolean =
    js(
        """
        (function(d, k) {
            try {
                var obj = d;
                if (typeof d === 'string') { obj = JSON.parse(d); }
                if (obj && obj.type === 'DROIDDECK_STATUS' && obj[k] !== undefined) {
                    return Boolean(obj[k]);
                }
            } catch (e) {}
            return false;
        })(data, key)
        """
    )

@OptIn(ExperimentalWasmJsInterop::class)
private fun extractStatusInt(data: JsAny?, key: String, defaultVal: Int): Int =
    js(
        """
        (function(d, k, def) {
            try {
                var obj = d;
                if (typeof d === 'string') { obj = JSON.parse(d); }
                if (obj && obj.type === 'DROIDDECK_STATUS' && typeof obj[k] === 'number') {
                    return obj[k] | 0;
                }
            } catch (e) {}
            return def;
        })(data, key, defaultVal)
        """
    )

@OptIn(ExperimentalComposeUiApi::class)
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

        LaunchedEffect(Unit) {
            val listener: (org.w3c.dom.events.Event) -> Unit = { event ->
                val msgEvent = event as? MessageEvent
                val data = msgEvent?.data
                if (data != null) {
                    val statusType = extractStatusString(data, "type")
                    if (statusType == "DROIDDECK_STATUS") {
                        ready = extractStatusBool(data, "ready")
                        busy = extractStatusBool(data, "busy")
                        stage = extractStatusString(data, "stage") ?: ""
                        percent = extractStatusInt(data, "percent", -1)
                        installed = extractStatusString(data, "installed")
                        available = extractStatusString(data, "available")
                    }
                }
            }
            window.addEventListener("message", listener)
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
