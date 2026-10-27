package com.droiddeck.launcher

object R {
    object string {
        const val app_name = 1
        const val games_play_steam = 2
        const val games_play_steamvr = 3
        const val games_runtime_first_play = 4
        const val games_runtime_update = 5
        const val games_runtime_ready = 6
        const val games_runtime_busy = 7
        const val games_runtime_busy_percent = 8
        const val setup_runtime_progress = 9
        const val runtime_removal_incomplete = 10
        const val hop_from_play = 11
        const val setup_account = 12
        const val setup_offline = 13
        const val setup_signed_in = 14
        const val setup_signed_in_unnamed = 15
        const val setup_signed_in_offline = 16
        const val setup_signed_in_offline_unnamed = 17
        const val setup_sign_in_first = 18
        const val vr_controllers_tab = 19
        const val drawer_page_settings = 20
        const val drawer_page_display = 21
        const val drawer_page_controls = 22
        const val mode_audio = 23
        const val mode_session = 24
        const val stores_tab_library = 25
        const val mode_tab_steam = 26
        const val setup_tool_files = 27
        const val common_close = 28
    }

    private val strings = mapOf(
        string.app_name to "DroidDeck",
        string.games_play_steam to "Play Steam",
        string.games_play_steamvr to "Play SteamVR",
        string.games_runtime_first_play to "Runtime installs on first Play",
        string.games_runtime_update to "Runtime update available",
        string.games_runtime_ready to "● Runtime ready",
        string.games_runtime_busy to "Runtime busy",
        string.games_runtime_busy_percent to "Runtime busy · %1\$d%%",
        string.setup_runtime_progress to "%1\$s · %2\$d%%",
        string.runtime_removal_incomplete to "Removal incomplete",
        string.hop_from_play to "Play settings",
        string.setup_account to "Steam account",
        string.setup_offline to "Offline mode",
        string.setup_signed_in to "Signed in as %1\$s",
        string.setup_signed_in_unnamed to "Signed in",
        string.setup_signed_in_offline to "Signed in as %1\$s · offline mode",
        string.setup_signed_in_offline_unnamed to "Signed in · offline mode",
        string.setup_sign_in_first to "Sign in to Steam first",
        string.vr_controllers_tab to "Controllers",
        string.drawer_page_settings to "Settings",
        string.drawer_page_display to "Display",
        string.drawer_page_controls to "Controls",
        string.mode_audio to "Audio",
        string.mode_session to "Session",
        string.stores_tab_library to "Library",
        string.mode_tab_steam to "Steam",
        string.setup_tool_files to "Files",
        string.common_close to "Close",
    )

    fun resolveString(id: Int, vararg args: Any): String {
        val template = strings[id] ?: ""
        if (args.isEmpty()) return template
        var result = template
        for ((index, arg) in args.withIndex()) {
            val i = index + 1
            result = result.replace("%" + i + "\$s", arg.toString())
                .replace("%" + i + "\$d", arg.toString())
        }
        return result
    }
}
