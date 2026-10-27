package com.droiddeck.launcher.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.ProvidableCompositionLocal
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.res.stringResource
import com.droiddeck.launcher.R
import com.droiddeck.launcher.frontend.Library

val LocalNarrowPane: ProvidableCompositionLocal<Boolean> = staticCompositionLocalOf { false }

val LocalFrontFocus: ProvidableCompositionLocal<FrontFocus?> = staticCompositionLocalOf { null }

class FrontFocus {
    val rail = HashMap<String, FocusRequester>()
    val primary = FocusRequester()
    var primaryAttached by mutableStateOf(0)
    var focusedRail by mutableStateOf<String?>(null)
    fun railFor(key: String): FocusRequester = rail.getOrPut(key) { FocusRequester() }
    val items = HashMap<String, FocusRequester>()
    val attached = HashMap<String, Int>()
    var last by mutableStateOf<String?>(null)
    val firstTile = FocusRequester()
    var firstTileAttached = 0

    fun paneEntry(): FocusRequester {
        val id = last
        return when {
            id == PRIMARY && primaryAttached > 0 -> primary
            id != null && id != PRIMARY && (attached[id] ?: 0) > 0 -> items.getValue(id)
            primaryAttached > 0 -> primary
            else -> FocusRequester.Default
        }
    }

    companion object {
        const val PRIMARY = "\u0000primary"
    }
}

class FrontEndState(
    val installed: String? = null,
    val ready: Boolean = false,
    val available: String? = null,
    val busy: Boolean = false,
    val stage: String = "",
    val percent: Int = -1,
    val desktopInstalled: Boolean = false,
    val desktopPresent: Boolean = desktopInstalled,
    val offlineAccount: String? = null,
    val offline: Boolean = false,
    val frameGenLabel: String = "",
    val romsDir: String? = null,
    val logsEnabled: Boolean = false,
    val vacDiagnostics: Boolean = false,
    val agentCommands: Boolean = false,
    val clearCachedApps: Boolean = false,
    val adbPaired: Boolean = false,
    val backgroundAppLimit: Int = -1,
    val memoryHud: String = "off",
    val steamGames: List<Library.SteamGame> = emptyList(),
    val steamGamesX64: Boolean = false,
    val removalPending: Boolean = false,
)

class FrontEndActions(
    val onPlay: () -> Unit = {},
    val onPlaySteamVr: () -> Unit = {},
    val onPlayDesktopUi: () -> Unit = {},
    val onSteamUrl: (String) -> Unit = {},
    val onSteamGame: (Library.SteamGame) -> Unit = {},
    val onDesktop: () -> Unit = {},
    val onResume: () -> Unit = {},
    val onSteamSettings: () -> Unit = {},
    val onDesktopSettings: () -> Unit = {},
    val modeSheet: @Composable (mode: String, tab: String, extras: SheetExtras) -> Unit = { _, _, _ -> },
    val onOffline: () -> Unit = {},
)

fun modeSheetTabs(steam: Boolean): List<String> =
    if (steam) listOf(SheetTab.STEAM, SheetTab.DISPLAY, SheetTab.CONTROLS, SheetTab.AUDIO, SheetTab.SESSION, SheetTab.LIBRARY)
    else listOf(SheetTab.DISPLAY, SheetTab.CONTROLS, SheetTab.SESSION)

@Composable
fun sheetTabLabel(tab: String): String = when (tab) {
    SheetTab.DISPLAY -> stringResource(R.string.drawer_page_display)
    SheetTab.CONTROLS -> stringResource(R.string.drawer_page_controls)
    SheetTab.AUDIO -> stringResource(R.string.mode_audio)
    SheetTab.SESSION -> stringResource(R.string.mode_session)
    SheetTab.LIBRARY -> stringResource(R.string.stores_tab_library)
    SheetTab.STEAM -> stringResource(R.string.mode_tab_steam)
    else -> stringResource(R.string.setup_tool_files)
}

class SheetExtras(
    val frameGen: @Composable () -> Unit = {},
    val performance: @Composable () -> Unit = {},
    val drivers: @Composable (summary: String?) -> Unit = {},
    val account: @Composable (host: Any?) -> Unit = {},
)

@Composable
fun FrameGenGroup(s: FrontEndState, a: FrontEndActions) {}

@Composable
fun PerformanceLink(a: FrontEndActions, mode: String, tab: String) {}

@Composable
fun DriversLink(a: FrontEndActions, summary: String?, mode: String, back: String) {}

@Composable
fun SettingsGroup(title: String, content: @Composable () -> Unit) {
    content()
}

@Composable
fun ToggleRow(
    host: Any?,
    key: String,
    title: String,
    subtitle: String,
    checked: Boolean,
    enabled: Boolean,
    onToggle: () -> Unit,
) {}
