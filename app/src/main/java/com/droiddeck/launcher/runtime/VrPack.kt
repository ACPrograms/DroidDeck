package com.droiddeck.launcher.runtime

import android.content.Context
import android.util.Log
import com.droiddeck.launcher.R
import com.droiddeck.launcher.core.AddonPack
import com.droiddeck.launcher.core.FileUtils
import com.droiddeck.launcher.handtracking.HandTracking
import com.droiddeck.launcher.session.SessionPhase
import com.droiddeck.launcher.session.SessionState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.io.FileInputStream
import java.nio.ByteBuffer
import java.nio.channels.FileChannel
import java.util.concurrent.Executors

/** The SteamVR driver and layer, hand-tracking models, OpenCV/MediaPipe native libraries and Luma calibration, downloaded on first use. */
internal object VrPack {
    data class Removal(val busy: Boolean = false, val error: String? = null)

    const val HAND = "hand_landmarker.task"
    const val HAND_LITE = "hand_landmarker_lite.task"
    const val POSE = "pose_landmarker_lite.task"
    const val LUMA_CALIBRATION = "luma-ultra-640x480.json"
    private const val OPENCV = "libopencv_java4.so"
    private const val MEDIAPIPE = "libmediapipe_tasks_jni.so"
    private const val GUEST = "usr/local/lib/droiddeck-vr"
    private const val IMPLICIT_LAYER = "usr/share/vulkan/implicit_layer.d/droiddeck_vr.json"
    private const val PACKED_LAYER = "layer/VK_LAYER_DROIDDECK_vr_implicit.json"
    private const val MARKER = ".pack"
    private const val LEGACY = "addons/tracking"
    private const val STEAMVR = "opt/steamvr/bin/linuxarm64/vrstartup"
    private const val TAG = "VrPack"
    private val TREES = listOf("droiddeck", "layer")
    private val IDLE = setOf(SessionPhase.IDLE, SessionPhase.FAILED)

    private val pack = AddonPack.VR
    val state get() = pack.state
    val size get() = pack.size
    private val lock = Any()
    private val guest = Any()
    @Volatile private var nativeLoaded = false
    private val worker = Executors.newSingleThreadExecutor { Thread(it, "vr-pack").apply { isDaemon = true } }
    private val mutableRemoval = MutableStateFlow(Removal())
    val removal: StateFlow<Removal> = mutableRemoval.asStateFlow()

    fun init(context: Context) {
        val app = context.applicationContext
        val root = LinuxRuntime.rootDir(app)
        val legacy = File(app.filesDir, LEGACY)
        val used = pack.outdated(app) || legacy.listFiles().orEmpty().any { File(it, AddonPack.COMPLETE).isFile } ||
            File(root, STEAMVR).isFile && File(root, GUEST).isDirectory && !File(root, "$GUEST/$MARKER").isFile
        legacy.deleteRecursively()
        if (pack.refresh(app) != AddonPack.State.Ready && used) ensure(app)
    }

    fun installed(context: Context) = pack.installed(context)
    fun refresh(context: Context) = pack.refresh(context)
    fun ensure(context: Context) = pack.ensure(context) { app ->
        HandTracking.load(app)
        if (SessionState.running || SessionState.phase !in IDLE) stageGuest(app, LinuxRuntime.rootDir(app))
    }

    fun await(context: Context, running: () -> Boolean, onState: (AddonPack.State) -> Unit): Boolean =
        pack.await(context, running, onState)

    fun file(context: Context, name: String): File = pack.file(context, name)

    fun installedBytes(context: Context): Long = pack.dir(context).walkBottomUp().filter { it.isFile }.sumOf { it.length() }

    fun model(context: Context, name: String): ByteBuffer =
        FileInputStream(file(context, name)).use { it.channel.map(FileChannel.MapMode.READ_ONLY, 0, it.channel.size()) }

    /**
     * Loads OpenCV and MediaPipe from the pack. MediaPipe's own System.loadLibrary then resolves to the
     * already-loaded library by soname, so this must run before any MediaPipe task class initializes.
     */
    fun loadNative(context: Context): Boolean {
        if (nativeLoaded) return true
        synchronized(lock) {
            if (nativeLoaded) return true
            if (!pack.installed(context)) return false
            return runCatching {
                System.load(file(context, OPENCV).absolutePath)
                System.load(file(context, MEDIAPIPE).absolutePath)
                nativeLoaded = true
                true
            }.onFailure { Log.e(TAG, "native libraries failed to load", it) }.getOrDefault(false)
        }
    }

    /** Blocks the calling worker thread until the pack is usable, starting the download if needed. */
    fun awaitNative(context: Context, running: () -> Boolean, onState: (AddonPack.State) -> Unit): Boolean =
        pack.await(context, running, onState) && loadNative(context)

    fun stageGuest(context: Context, root: File) = synchronized(guest) {
        val tree = File(root, GUEST)
        val layer = File(root, IMPLICIT_LAYER)
        if (!pack.installed(context)) {
            FileUtils.delete(tree)
            FileUtils.delete(layer)
            return@synchronized
        }
        val dir = pack.dir(context)
        if (FileUtils.readString(File(tree, MARKER)) == dir.name && layer.isFile) return@synchronized
        runCatching {
            FileUtils.delete(tree)
            TREES.forEach { copy(File(dir, it), File(tree, it)) }
            copy(File(dir, PACKED_LAYER), layer)
            check(FileUtils.writeString(File(tree, MARKER), dir.name))
        }.onFailure { Log.w(TAG, "could not stage the VR pack", it) }
    }

    fun uninstall(context: Context) {
        val app = context.applicationContext
        worker.execute {
            mutableRemoval.value = Removal(busy = true)
            val maintenance = LinuxRuntimeInstaller.beginMaintenance(app, app.getString(R.string.vr_module_removing))
            val error = when {
                maintenance == null -> app.getString(R.string.vr_module_busy)
                maintenance.run(null) { remove(app) } -> null
                else -> LinuxRuntimeInstaller.removalError() ?: app.getString(R.string.vr_module_remove_failed)
            }
            if (error == null) HandTracking.setEnabled(app, false)
            mutableRemoval.value = Removal(error = error)
        }
    }

    private fun remove(context: Context): String? = synchronized(guest) {
        if (!pack.remove(context)) return@synchronized context.getString(R.string.vr_module_remove_failed)
        val root = LinuxRuntime.rootDir(context)
        FileUtils.delete(File(root, GUEST))
        FileUtils.delete(File(root, IMPLICIT_LAYER))
        null
    }

    private fun copy(source: File, target: File) {
        if (source.isDirectory) {
            source.listFiles().orEmpty().forEach { copy(it, File(target, it.name)) }
            return
        }
        target.parentFile?.mkdirs()
        val staged = File(target.path + ".staged")
        source.inputStream().use { input -> staged.outputStream().use { FileUtils.copy(input, it) } }
        staged.setExecutable(true, false)
        check(staged.renameTo(target)) { "could not stage ${target.name}" }
    }
}
