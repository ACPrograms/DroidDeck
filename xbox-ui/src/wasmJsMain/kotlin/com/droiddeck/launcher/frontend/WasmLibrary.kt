package com.droiddeck.launcher.frontend

import java.io.File

object Library {
    data class SteamGame(
        val gameId: String = "",
        val appId: Int = 0,
        val name: String = "",
        val lastPlayed: Long = 0L,
        val art: Any? = null,
    )

    fun steamLibraryArt(ctx: Any?): List<Pair<Int, File>> = emptyList()
}
