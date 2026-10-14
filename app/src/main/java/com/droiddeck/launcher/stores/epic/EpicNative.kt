// JNI symbols depend on this package path and class name (app/src/main/rust/stores/JNI.md).
package com.droiddeck.launcher.stores.epic

import android.util.Log
import com.droiddeck.launcher.stores.StoresNative
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/**
 * The native Epic engines in `libdroiddeckstores.so`. [stream] is the install the manager uses
 * whenever the library loads: chunks fetched in parallel by the adaptive fetch core and written
 * straight into each file's staging file as they arrive, shared chunks kept in a cache of at most
 * the given budget, every file verified and swapped in with the same rules as the bounded Java
 * writer (see `stream.rs`). [run] and [assemble] are the older batch pair: [run] fills the chunk
 * cache with verified, decompressed chunks for the pending files, [assemble] writes the files from
 * it. They stay for callers that explicitly want a whole-game cache.
 *
 * The cache is `<installDir>/.chunks` when `chunkCacheDir` is `""`, else the scratch folder given -
 * the app's own cache when the game installs to a card.
 *
 * All block while polling [AtomicBoolean] `cancel` every 250 ms. [run] and [assemble] flip the
 * native flag on cancel and wait up to 5 s; [stream] waits for the run to finish, because its
 * cleanup (removing unfinished staging files) must be done before the manager returns.
 */
object EpicNative {
    private const val TAG = "EpicNative"

    /** Callbacks from a native run; every method is called on a native thread. One interface serves both runs. */
    interface Listener {
        /** Fetch only, once before any fetch: the plan the engine derived (the manager cross-checks it). */
        fun onPlan(chunksTotal: Int, bytesTotal: Long, chunkDir: String)
        /** Fetch: per accounted chunk (cached-skip or fetched), done/total = chunks. Assembly: per file written, done/total = files. */
        fun onProgress(bytesDone: Long, bytesTotal: Long, done: Int, total: Int)
        /** Engine log line (already in logcat under `EpicNative`). */
        fun onLog(line: String)
        /** Terminal. Fetch: bytes credited; assembly: bytes written. */
        fun onComplete(success: Boolean, error: String, bytes: Long)
    }

    /** Callbacks from a [stream] run; every method is called on a native thread. */
    interface StreamListener {
        /** File bytes written so far (staging included), and the files verified and swapped in so far. */
        fun onStreamProgress(bytesWritten: Long, bytesTotal: Long, filesDone: Int, filesTotal: Int, filesBytes: Long)
        /** Engine log line (already in logcat under `EpicNative`). */
        fun onLog(line: String)
        /** Once, before the first file replaces its destination; false (or a throw) fails the run with nothing replaced. */
        fun onBeforeReplace(): Boolean
        /** Terminal; `bytes` = file bytes written. */
        fun onComplete(success: Boolean, error: String, bytes: Long)
    }

    /** Outcome of a run. `started == false`: nothing happened on disk, the caller runs its own loop. */
    class Result(
        @JvmField val started: Boolean, @JvmField val success: Boolean, @JvmField val cancelled: Boolean,
        @JvmField val error: String, @JvmField val bytes: Long, @JvmField val done: Int, @JvmField val total: Int,
    )

    private class Completion(val success: Boolean, val error: String, val bytes: Long)

    /** The fetch: chunks for [pendingFileIdx] into the cache. */
    @JvmStatic
    fun run(
        manifest: ByteArray, installDir: String, chunkCacheDir: String, cdnPrefixes: Array<String>, pendingFileIdx: IntArray, expectedChunks: Int, expectedBytes: Long,
        caBundlePath: String, maxWorkers: Int, processWorkers: Int, cancel: AtomicBoolean?, listener: Listener,
    ): Result = drive("nativeStart", cancel, listener) { inner ->
        nativeStart(manifest, installDir, chunkCacheDir, cdnPrefixes, pendingFileIdx, expectedChunks, expectedBytes, caBundlePath, maxWorkers, processWorkers, inner)
    }

    /** The assembly: the files [fileIdx] written from the cache, the cache removed on success. */
    @JvmStatic
    fun assemble(manifest: ByteArray, installDir: String, chunkCacheDir: String, fileIdx: IntArray, cancel: AtomicBoolean?, listener: Listener): Result =
        drive("nativeAssemble", cancel, listener) { inner -> nativeAssemble(manifest, installDir, chunkCacheDir, fileIdx, inner) }

    /**
     * The adaptive install of the files [pendingFileIdx] (indices into the manifest's file list),
     * with at most [cacheBudget] bytes of shared chunks cached. `started == false` means nothing
     * was fetched or written and the caller runs the bounded Java writer.
     */
    @JvmStatic
    fun stream(
        manifest: ByteArray, installDir: String, chunkCacheDir: String, cdnPrefixes: Array<String>, pendingFileIdx: IntArray, expectedBytes: Long,
        cacheBudget: Long, caBundlePath: String, maxWorkers: Int, processWorkers: Int, cancel: AtomicBoolean?, listener: StreamListener,
    ): Result {
        if (!StoresNative.ensureLoaded()) return Result(false, false, false, "engine not built", 0L, 0, 0)
        val latch = CountDownLatch(1)
        val completion = AtomicReference<Completion?>(null)
        val doneRef = AtomicReference(0)
        val totalRef = AtomicReference(pendingFileIdx.size)
        val inner = object : StreamListener {
            override fun onStreamProgress(bytesWritten: Long, bytesTotal: Long, filesDone: Int, filesTotal: Int, filesBytes: Long) {
                doneRef.set(filesDone); totalRef.set(filesTotal)
                listener.onStreamProgress(bytesWritten, bytesTotal, filesDone, filesTotal, filesBytes)
            }
            override fun onLog(line: String) = listener.onLog(line)
            override fun onBeforeReplace(): Boolean = runCatching { listener.onBeforeReplace() }.getOrDefault(false)
            override fun onComplete(success: Boolean, error: String, bytes: Long) {
                completion.set(Completion(success, error, bytes))
                latch.countDown()
                runCatching { listener.onComplete(success, error, bytes) }
            }
        }
        val handle: Long = try {
            nativeStream(manifest, installDir, chunkCacheDir, cdnPrefixes, pendingFileIdx, expectedBytes, cacheBudget, caBundlePath, maxWorkers, processWorkers, inner)
        } catch (t: Throwable) {
            Log.w(TAG, "engine unavailable: ${t.javaClass.simpleName}: ${t.message}")
            return Result(false, false, false, "nativeStream: ${t.javaClass.simpleName}", 0L, 0, 0)
        }
        if (handle == 0L) return Result(false, false, false, completion.get()?.error ?: "not started", 0L, 0, totalRef.get())
        var cancelSent = false
        try {
            while (!latch.await(250, TimeUnit.MILLISECONDS)) {
                if (!cancelSent && cancel != null && cancel.get()) {
                    nativeCancel(handle)
                    cancelSent = true
                }
            }
        } finally {
            nativeRelease(handle)
        }
        val c = completion.get()
        val cancelled = cancelSent || (c != null && !c.success && c.error == "cancelled")
        val success = !cancelled && c != null && c.success
        val error = when { cancelled -> "cancelled"; c == null -> "no completion"; else -> c.error }
        return Result(true, success, cancelled, error, c?.bytes ?: 0L, doneRef.get(), totalRef.get())
    }

    private fun drive(what: String, cancel: AtomicBoolean?, listener: Listener, start: (Listener) -> Long): Result {
        if (!StoresNative.ensureLoaded()) return Result(false, false, false, "engine not built", 0L, 0, 0)
        val latch = CountDownLatch(1)
        val completion = AtomicReference<Completion?>(null)
        val doneRef = AtomicReference(0)
        val totalRef = AtomicReference(0)
        val inner = object : Listener {
            override fun onPlan(chunksTotal: Int, bytesTotal: Long, chunkDir: String) { totalRef.set(chunksTotal); listener.onPlan(chunksTotal, bytesTotal, chunkDir) }
            override fun onProgress(bytesDone: Long, bytesTotal: Long, done: Int, total: Int) { doneRef.set(done); totalRef.set(total); listener.onProgress(bytesDone, bytesTotal, done, total) }
            override fun onLog(line: String) = listener.onLog(line)
            override fun onComplete(success: Boolean, error: String, bytes: Long) {
                completion.set(Completion(success, error, bytes))
                latch.countDown()
                runCatching { listener.onComplete(success, error, bytes) }
            }
        }
        val handle: Long = try {
            start(inner)
        } catch (t: Throwable) {
            Log.w(TAG, "engine unavailable: ${t.javaClass.simpleName}: ${t.message}")
            return Result(false, false, false, "$what: ${t.javaClass.simpleName}", 0L, 0, 0)
        }
        if (handle == 0L) return Result(false, false, false, completion.get()?.error ?: "not started", 0L, 0, totalRef.get())
        var cancelSent = false
        try {
            while (!latch.await(250, TimeUnit.MILLISECONDS)) {
                if (cancel != null && cancel.get()) {
                    nativeCancel(handle)
                    cancelSent = true
                    latch.await(5, TimeUnit.SECONDS)
                    break
                }
            }
        } finally {
            nativeRelease(handle)
        }
        val c = completion.get()
        val success = !cancelSent && c != null && c.success
        val error = when { cancelSent -> "cancelled"; c == null -> "no completion"; else -> c.error }
        return Result(true, success, cancelSent, error, c?.bytes ?: 0L, doneRef.get(), totalRef.get())
    }

    @JvmStatic
    private external fun nativeStart(
        manifest: ByteArray, installDir: String, chunkCacheDir: String, cdnPrefixes: Array<String>, pendingFileIdx: IntArray, expectedChunks: Int, expectedBytes: Long,
        caBundlePath: String, maxWorkers: Int, processWorkers: Int, listener: Listener,
    ): Long

    @JvmStatic
    private external fun nativeAssemble(manifest: ByteArray, installDir: String, chunkCacheDir: String, fileIdx: IntArray, listener: Listener): Long

    @JvmStatic
    private external fun nativeStream(
        manifest: ByteArray, installDir: String, chunkCacheDir: String, cdnPrefixes: Array<String>, pendingFileIdx: IntArray, expectedBytes: Long,
        cacheBudget: Long, caBundlePath: String, maxWorkers: Int, processWorkers: Int, listener: StreamListener,
    ): Long

    @JvmStatic
    private external fun nativeCancel(handle: Long)

    @JvmStatic
    private external fun nativeRelease(handle: Long)
}
