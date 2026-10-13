package com.droiddeck.launcher.core

import android.content.Context
import android.os.Build
import com.droiddeck.launcher.R
import com.droiddeck.launcher.gpu.SystemVulkanDriver
import java.io.File

/** Whether this device has a graphics backend the runtime can use. Adreno uses Turnip;
 * PowerVR uses its vendor Vulkan driver through the bundled runtime bridge. Other GPU families
 * still need a supported backend before a session can display frames. */
object DeviceSupport {
    fun adreno(): Boolean =
        File("/sys/class/kgsl/kgsl-3d0").exists() || File("/vendor/lib64/hw/vulkan.adreno.so").exists()

    fun graphicsAvailable(): Boolean = graphicsAvailable(adreno(), SystemVulkanDriver.isAvailable())

    internal fun graphicsAvailable(adreno: Boolean, powerVrReady: Boolean): Boolean = adreno || powerVrReady

    /**
     * Whether the Linux side draws through the Vulkan bridge (tools/vkbridge, PR #517): asked for in
     * Setup, and only on a GPU that is neither an Adreno nor one the system-Vulkan path (PowerVR,
     * Xclipse) already handles. An Adreno keeps Turnip whatever the setting says.
     */
    fun vkBridge(context: Context): Boolean =
        !adreno() && !SystemVulkanDriver.isAvailable() && !openDriverActive(context) &&
            com.droiddeck.launcher.session.SessionPrefs.vkBridge(context)

    /**
     * Whether an open non-Adreno driver pair (PanVK for Mali, RADV for Xclipse) is the one in use:
     * it and the bridge are alternatives, and the pair wins. Always false on an Adreno.
     */
    fun openDriverActive(context: Context): Boolean {
        if (adreno()) return false
        // The runtime half is what counts: a Mali pair may run with the system driver as its display.
        val linux = com.droiddeck.launcher.session.SessionPrefs.linuxDriver(context).ifEmpty { return false }
        return com.droiddeck.launcher.gpu.DriverPairs.isNonAdreno(com.droiddeck.launcher.gpu.DriverBundle.containing(context, linux, true)?.id)
    }

    /**
     * A session can draw: Adreno with Turnip, PowerVR/Xclipse on the system driver, an open
     * non-Adreno driver pair, or the bridge.
     */
    fun canDraw(context: Context): Boolean = graphicsAvailable() || openDriverActive(context) || vkBridge(context)

    /** The chip as the device names it, for the card that explains the refusal. */
    fun gpuName(context: Context): String {
        val soc = if (Build.VERSION.SDK_INT >= 31) Build.SOC_MODEL.takeIf { it.isNotBlank() && it != Build.UNKNOWN } else null
        return soc?.let { "$it (${Build.HARDWARE})" } ?: Build.HARDWARE.ifBlank { context.getString(R.string.gpu_this_gpu) }
    }

    /** False on the Odin 3 (CQ8725S): a low-latency Wi-Fi lock makes its WLAN firmware hang, and
     *  that firmware reboots the whole device instead of restarting Wi-Fi. */
    fun lowLatencyWifiSafe(): Boolean = Build.VERSION.SDK_INT < 31 || Build.SOC_MODEL != "CQ8725S"
}
