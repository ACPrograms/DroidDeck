package com.droiddeck.launcher.gpu

import com.droiddeck.launcher.gpu.GpuInfo.Family

/**
 * The release drivers as matched pairs: a display build (the app's compositor, bionic) and a Linux
 * build (the runtime, glibc) that were made to run together. Mixing a runtime driver carrying a
 * patch with a display driver that lacks it is where the hard-to-read bugs come from, so the app
 * installs and selects both halves of one pair at once.
 *
 * - Banners-Turnip publishes each variant as a set in every release (`Turnip-<tag><variant>.zip`
 *   and `...-Linux.zip`), rebuilt from upstream Mesa as it moves: the pair is one variant of the
 *   newest release that carries both halves.
 * - WinNative ships its display (`v1.x`) and Linux (`linux-v0.x`) builds as two release lines with
 *   their own numbers: the pair is the newest of each, of one flavour (Balanced or Performance).
 * - DroidDeck's DD-Turnip ships both halves in one zip per release (DriverBundle): the pair is the
 *   newest bundle, and it can never be mixed.
 */
object DriverPairs {
    const val BANNER = "banner"
    const val BANNER_A8XX = "banner-a8xx"
    const val BANNER_710 = "banner-710-720-test"
    const val BANNER_ONEUI = "banner-8g2-oneui"
    const val WN_BALANCED = "wn-b"
    const val WN_PERFORMANCE = "wn-p"
    const val DD_TURNIP = "dd-turnip"
    /** Each DD-Turnip release is its own pair: "dd-turnip:DD-Turnip-v0.1.0". */
    fun ddTurnip(tag: String) = "$DD_TURNIP:$tag"

    /** The open non-Adreno drivers (The412Banner/droiddeck-nonadreno-drivers), one pair per driver. */
    const val NON_ADRENO = "nad"
    const val NAD_RADV_XCLIPSE = "nad:radv-xclipse"
    const val NAD_MALI_JM = "nad:panvk-mali-jm"
    const val NAD_MALI_G710 = "nad:panvk-mali-g710"
    const val NAD_MALI_G615 = "nad:panvk-mali-g615"
    const val NAD_MALI_G720 = "nad:panvk-mali-g720"
    /** One PanVK kbase binary for Mali v9-v13; its display half needs Android 15. */
    const val NAD_MALI_KBASE = "nad:panvk-mali-kbase"
    const val MALI_KBASE_MIN_SDK = 35
    /** faux123's PanVK builds for three handhelds: manual choices, never Auto. */
    const val NAD_FAUX_RP4PRO = "nad:panvk-faux123-rp4pro"
    const val NAD_FAUX_ACE = "nad:panvk-faux123-ace"
    const val NAD_FAUX_RG556 = "nad:panvk-faux123-rg556"
    /** The one Mali a device-specific build is for, by the number in the GPU's name. */
    private val DEVICE_BUILDS = mapOf(NAD_FAUX_RP4PRO to 77, NAD_FAUX_ACE to 610, NAD_FAUX_RG556 to 57)
    /** "PanVK-Mali-G720" (the release asset's id) to its pair key, "nad:panvk-mali-g720". */
    fun nonAdreno(id: String) = "$NON_ADRENO:" + id.lowercase()
    /** True for a non-Adreno pair's key, or a bundle installed from one ("PanVK-Mali-G720"). */
    fun isNonAdreno(keyOrBundleId: String?): Boolean = keyOrBundleId != null &&
        (keyOrBundleId.startsWith("$NON_ADRENO:") || Regex("""^(?i)(RADV-Xclipse|PanVK-[A-Za-z0-9]+)(-[A-Za-z0-9]+)*$""").matches(keyOrBundleId))

    fun nonAdrenoName(key: String): String = describeNonAdreno(key).first

    private fun describeNonAdreno(key: String): Pair<String, Set<Family>> = when (key) {
        NAD_RADV_XCLIPSE -> "RADV · Samsung Xclipse" to setOf(Family.POWERVR)
        NAD_MALI_JM -> "PanVK · Mali G52-G78" to setOf(Family.MALI_JM)
        NAD_MALI_G710 -> "PanVK · Mali-G610/G710" to setOf(Family.MALI_G610_G710)
        NAD_MALI_G615 -> "PanVK · Mali-G615" to setOf(Family.MALI_G615)
        NAD_MALI_G720 -> "PanVK · Mali-G720" to setOf(Family.MALI_G720)
        NAD_FAUX_RP4PRO -> "PanVK faux123 · Retroid Pocket 4 Pro (Mali-G77)" to setOf(Family.MALI_JM)
        NAD_FAUX_ACE -> "PanVK faux123 · GameForce Ace (Mali-G610)" to setOf(Family.MALI_G610_G710)
        NAD_FAUX_RG556 -> "PanVK faux123 · Anbernic RG556 (Mali-G57)" to setOf(Family.MALI_JM)
        NAD_MALI_KBASE -> "PanVK kbase · Mali G57-G925" to
            setOf(Family.MALI_JM, Family.MALI_G610_G710, Family.MALI_G615, Family.MALI_G720, Family.MALI_LATEST)
        else -> key.removePrefix("$NON_ADRENO:") to emptySet<Family>()
    }

    class DriverPair(
        val key: String,
        /** "Banners-Turnip · Adreno 6xx/7xx", "WinNative · Balanced". */
        val name: String,
        /** The families the pair is built for; the list only offers it on those unless asked. */
        val families: Set<Family>,
        val display: TurnipReleases.Asset?,
        val linux: TurnipReleases.Asset?,
        /** Both halves in one zip, instead of [display] and [linux]. */
        val bundle: TurnipReleases.Asset? = null,
    ) {
        val complete: Boolean get() = bundle != null || (display != null && linux != null)
        /** The zips to download for the pair: the bundle, or the two halves. */
        val assets: List<TurnipReleases.Asset> get() = listOfNotNull(bundle ?: display, if (bundle == null) linux else null)
        /** "r4 · 1.19 + 0.1.2": the build of each half. */
        val version: String
            get() = listOfNotNull(bundle?.tag, display?.tag, linux?.tag).distinct().joinToString(" + ") { it.removePrefix("linux-").removePrefix("DD-Turnip-") }
        fun suits(gpu: GpuInfo) = when {
            // The system-driver family covers PowerVR and Xclipse; RADV is only for the Xclipse.
            key == NAD_RADV_XCLIPSE -> gpu.family == Family.POWERVR && gpu.name.contains("Xclipse", ignoreCase = true)
            // One binary for Mali v9-v13, not the older G52/G76 in the Job Manager family.
            key == NAD_MALI_KBASE -> gpu.family.mali && GpuInfo.maliKbaseCovered(gpu.name)
            key in DEVICE_BUILDS -> gpu.family.mali && GpuInfo.maliNumber(gpu.name) == DEVICE_BUILDS[key]
            else -> gpu.family in families ||
                (gpu.family == Family.ADRENO_UNKNOWN && (Family.A7XX in families || Family.A8XX in families))
        }
    }

    private val ALL_ADRENO = setOf(Family.A6XX, Family.A7XX, Family.A7XX_LOW, Family.A8XX, Family.ADRENO_UNKNOWN)

    private fun describe(key: String): Pair<String, Set<Family>> = when {
        key.startsWith(DD_TURNIP) -> "DroidDeck · DD-Turnip" to ALL_ADRENO
        key.startsWith("$NON_ADRENO:") -> describeNonAdreno(key)
        else -> describeFixed(key)
    }

    private fun describeFixed(key: String): Pair<String, Set<Family>> = when (key) {
        BANNER -> "Banners-Turnip · Adreno 6xx/7xx" to setOf(Family.A6XX, Family.A7XX)
        BANNER_A8XX -> "Banners-Turnip · Adreno 8xx" to setOf(Family.A8XX)
        BANNER_710 -> "Banners-Turnip · Adreno 710/720/722 (test)" to setOf(Family.A7XX_LOW)
        BANNER_ONEUI -> "Banners-Turnip · 8 Gen 2 on One UI" to setOf(Family.A7XX)
        WN_BALANCED -> "WinNative · Balanced" to ALL_ADRENO
        WN_PERFORMANCE -> "WinNative · Performance" to ALL_ADRENO
        else -> key to ALL_ADRENO
    }

    /** Every pair the last release check found, in the order the list shows them. */
    fun from(check: TurnipReleases.Check?): List<DriverPair> {
        val assets = check?.assets.orEmpty().filter { it.pair.isNotEmpty() }
        val order = listOf(DD_TURNIP, BANNER, BANNER_A8XX, BANNER_ONEUI, BANNER_710, WN_BALANCED, WN_PERFORMANCE, NON_ADRENO)
        return assets.map { it.pair }.distinct()
            .sortedBy { key -> order.indexOf(key.substringBefore(':')).let { i -> if (i < 0) order.size else i } }
            .map { key ->
                val (name, families) = describe(key)
                val halves = assets.filter { it.pair == key }
                halves.firstOrNull { it.bundle }?.let { return@map DriverPair(key, name, families, null, null, it) }
                // The list keeps the newest release of each half; for Banners-Turnip the two halves
                // must come from the same release, so a set missing one half in its newest release
                // is offered incomplete rather than mixed with an older one.
                val display = halves.firstOrNull { !it.linux }
                val linux = halves.firstOrNull { it.linux }
                val sameRelease = !key.startsWith(BANNER) || display?.tag == linux?.tag
                DriverPair(key, name, families, display, if (sameRelease) linux else null)
            }
    }

    /**
     * The pair Auto sets for [gpu], among the [pairs] the last release check found: the newest
     * official DD-Turnip release on the Adreno 840 (840 and 840v2), WinNative Balanced on the other
     * 8xx (Banners' A8xx builds still break on some first boots) and on an 840 while no DD-Turnip
     * release is listed, the OneUI build for an 8 Gen 2 under One UI, the patched test builds for
     * the 710/720/722, and upstream Banners-Turnip for the rest. Null when nothing fits (not Adreno).
     */
    fun recommendedKey(gpu: GpuInfo, pairs: List<DriverPair>, sdk: Int = android.os.Build.VERSION.SDK_INT): String? = when (gpu.family) {
        // Xclipse takes the RADV pair (testers: it boots to Steam where the system route's GL fails),
        // with the system route as its fallback; PowerVR keeps the system driver. A Mali no open
        // build covers draws through the Vulkan bridge (DriverMenus.ensureAuto).
        Family.POWERVR -> NAD_RADV_XCLIPSE.takeIf { gpu.name.contains("Xclipse", ignoreCase = true) }
        Family.NOT_ADRENO, Family.MALI_OTHER -> null
        Family.MALI_JM, Family.MALI_G610_G710, Family.MALI_G615, Family.MALI_G720, Family.MALI_LATEST -> maliKey(gpu, pairs, sdk)
        Family.A8XX -> if (gpu.model == 840) newestDdTurnip(pairs) ?: WN_BALANCED else WN_BALANCED
        Family.A7XX_LOW -> BANNER_710
        Family.A7XX -> if (gpu.oneUi8Gen2) BANNER_ONEUI else BANNER
        Family.A6XX -> BANNER
        // KGSL gave no model: Android 16 arrived with the 8xx, so a device that new is taken as one.
        Family.ADRENO_UNKNOWN -> if (android.os.Build.VERSION.SDK_INT >= 36) WN_BALANCED else BANNER
    }

    /**
     * A Mali's pair: the one-binary kbase build on Android 15+ for every Mali it covers, else the
     * build for the GPU's generation. A preferred pair the check did not find falls back to the
     * generation's own, and a G715/G725/G925 without the kbase build gets none (the bridge).
     */
    private fun maliKey(gpu: GpuInfo, pairs: List<DriverPair>, sdk: Int): String? {
        val family = when (gpu.family) {
            Family.MALI_JM -> NAD_MALI_JM
            Family.MALI_G610_G710 -> NAD_MALI_G710
            Family.MALI_G615 -> NAD_MALI_G615
            Family.MALI_G720 -> NAD_MALI_G720
            else -> null
        }
        val kbase = NAD_MALI_KBASE.takeIf { sdk >= MALI_KBASE_MIN_SDK && GpuInfo.maliKbaseCovered(gpu.name) }
        if (pairs.isEmpty()) return kbase ?: family
        fun listed(key: String?) = key?.takeIf { k -> pairs.any { it.key == k && it.complete } }
        return listed(kbase) ?: listed(family) ?: kbase ?: family
    }

    /** The complete DD-Turnip pair with the highest version, or null when none is listed. */
    fun newestDdTurnip(pairs: List<DriverPair>): String? = pairs
        .filter { it.key.startsWith("$DD_TURNIP:") && it.complete }
        .maxByOrNull { p ->
            Regex("""(\d+)\.(\d+)\.(\d+)$""").find(p.key)?.destructured
                ?.let { (major, minor, patch) -> major.toLong() * 1_000_000_000_000 + minor.toLong() * 1_000_000 + patch.toLong() } ?: -1L
        }?.key
}
