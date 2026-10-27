package com.droiddeck.launcher.frontend

object SteamFeatured {
    data class Game(
        val appId: Int = 0,
        val name: String = "",
        val art: Any? = null,
    )

    val games: List<Game> = emptyList()

    fun load(ctx: Any?) {}
}
