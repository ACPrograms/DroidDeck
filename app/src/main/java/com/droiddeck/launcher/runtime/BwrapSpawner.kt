package com.droiddeck.launcher.runtime

import android.content.Context
import android.net.LocalServerSocket
import android.net.LocalSocket
import android.os.Process
import android.system.Os
import android.util.Base64
import android.util.Log
import com.droiddeck.launcher.core.FileUtils
import com.droiddeck.launcher.core.HostProcess
import com.droiddeck.launcher.session.SessionPrefs
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.util.concurrent.atomic.AtomicInteger

object BwrapSpawner {
    private const val TAG = "BwrapSpawner"
    fun socket(context: Context): String = "${context.packageName}.bwrap"
    private val seq = AtomicInteger()
    @Volatile private var server: LocalServerSocket? = null

    @Synchronized
    fun start(context: Context) {
        if (server != null) return
        val app = context.applicationContext
        val name = socket(app)
        val socket = try {
            LocalServerSocket(name)
        } catch (e: IOException) {
            Log.e(TAG, "cannot listen on $name", e)
            return
        }
        server = socket
        FileUtils.delete(File(LinuxRuntime.liveDir(app), "bwrap"))
        Thread({
            while (true) {
                val client = try { socket.accept() } catch (e: IOException) { Log.e(TAG, "accept", e); break }
                Thread({ serve(app, client) }, "bwrap-${seq.get() + 1}").apply { isDaemon = true; start() }
            }
        }, "bwrap-server").apply { isDaemon = true; start() }
    }

    private fun serve(context: Context, client: LocalSocket) {
        val id = seq.incrementAndGet()
        val dir = File(LinuxRuntime.liveDir(context), "bwrap/$id")
        client.use {
            val out = DataOutputStream(client.outputStream)
            try {
                val peer = client.peerCredentials
                if (peer.uid != Process.myUid()) {
                    Log.w(TAG, "refused a sandbox for uid ${peer.uid}")
                    return
                }
                val input = DataInputStream(client.inputStream)
                val kind = input.readUnsignedByte()
                val body = ByteArray(input.readInt()).also { input.readFully(it) }
                if (kind != 'R'.code) return
                val request = JSONObject(String(body, Charsets.UTF_8))
                val plan = plan(context, request, dir)
                run(context, plan, client, out, id)
            } catch (e: Exception) {
                Log.e(TAG, "sandbox $id", e)
                runCatching {
                    frame(out, 'E', (e.message ?: e.toString()).toByteArray())
                    frame(out, 'X', "1".toByteArray())
                }
            } finally {
                FileUtils.delete(dir)
            }
        }
    }

    class Plan(
        val root: File, val binds: List<Bind>, val cwd: String, val argv: List<String>, val env: Map<String, String>,
        val launcher: List<String> = emptyList(),
    )

    data class Bind(val host: String, val guest: String) {
        val spec: String get() = if (host == guest) host else "$host:$guest"

        companion object {
            fun parse(spec: String): Bind {
                val colon = spec.indexOf(':')
                return if (colon < 0) Bind(spec, spec) else Bind(spec.substring(0, colon), spec.substring(colon + 1))
            }
        }
    }

    internal fun under(path: String, dir: String): Boolean =
        dir == "/" || path == dir || path.startsWith(if (dir.endsWith("/")) dir else "$dir/")

    internal fun hostPath(sessionBinds: List<Bind>, rootfs: String, guest: String): String {
        val bind = sessionBinds.filter { under(guest, it.guest) }.maxByOrNull { it.guest.length }
            ?: return rootfs.trimEnd('/') + guest
        val rest = guest.substring(bind.guest.length).trimStart('/')
        return if (rest.isEmpty()) bind.host else bind.host.trimEnd('/') + "/" + rest
    }

    internal fun bindsFor(sessionBinds: List<Bind>, rootfs: String, src: String, dest: String): List<Bind> {
        val out = ArrayList<Bind>()
        out.add(Bind(hostPath(sessionBinds, rootfs, src), dest))
        for (b in sessionBinds.sortedBy { it.guest.length }) {
            if (b.guest != src && under(b.guest, src)) {
                out.add(Bind(b.host, (dest.trimEnd('/') + "/" + b.guest.substring(src.length).trimStart('/'))))
            }
        }
        return out
    }

    internal fun plan(context: Context, request: JSONObject, dir: File): Plan {
        val rootfs = LinuxRuntime.rootDir(context).path
        val session = LinuxRuntime.lastBinds(context).map { Bind.parse(it) }
        val root = File(dir, "root").apply { mkdirs() }
        val binds = ArrayList<Bind>()
        var scratch = 0
        fun addBind(b: Bind) {
            binds.removeAll { it.guest == b.guest }
            binds.add(b)
        }
        fun coveringBind(dest: String) = binds.filter { under(dest, it.guest) }.maxByOrNull { it.guest.length }
        fun newScratch(): File = File(dir, "x${scratch++}")
        fun writable(dest: String): File? {
            val cover = coveringBind(dest) ?: return inRoot(root, dest)
            if (!under(cover.host, dir.path)) return null
            return inRoot(File(cover.host), dest.substring(cover.guest.length))
        }
        val ops = request.getJSONArray("ops")
        for (i in 0 until ops.length()) {
            val op = ops.getJSONObject(i)
            val dest = op.optString("dest")
            when (op.getString("t")) {
                "bind" -> {
                    val src = op.getString("src")
                    val all = bindsFor(session, rootfs, src, dest)
                    if (!File(all.first().host).exists()) {
                        if (!op.optBoolean("try")) Log.w(TAG, "sandbox source $src is missing; left out")
                        continue
                    }
                    all.forEach { addBind(it) }
                }
                "proc" -> bindsFor(session, rootfs, "/proc", dest).forEach { addBind(it) }
                "dev" -> bindsFor(session, rootfs, "/dev", dest).forEach { addBind(it) }
                "dir" -> {
                    val perms = op.optInt("perms", 0x1ed)
                    val here = writable(dest)
                    when {
                        op.optBoolean("empty") && coveringBind(dest) != null ->
                            addBind(Bind(newScratch().apply { mkdirs(); chmod(this, perms) }.path, dest))
                        here != null && (here.isDirectory || here.mkdirs()) -> chmod(here, perms)
                        File(hostPath(binds, root.path, dest)).isDirectory -> {}
                        else -> addBind(Bind(newScratch().apply { mkdirs(); chmod(this, perms) }.path, dest))
                    }
                }
                "file" -> {
                    val data = Base64.decode(op.getString("data"), Base64.DEFAULT)
                    val here = writable(dest)
                    val f = if (here != null && here.parentFile?.let { it.isDirectory || it.mkdirs() } == true) here
                            else newScratch().also { addBind(Bind(it.path, dest)) }
                    f.writeBytes(data)
                    chmod(f, op.optInt("perms", 0x1b6))
                }
                "symlink" -> {
                    val target = op.getString("target")
                    val link = writable(dest)
                    if (link != null) {
                        link.parentFile?.mkdirs()
                        if (!Files.isSymbolicLink(link.toPath()) && !link.exists()) Files.createSymbolicLink(link.toPath(), java.nio.file.Paths.get(target))
                    } else {
                        val abs = if (target.startsWith("/")) target else normalize(dest.substringBeforeLast('/') + "/" + target)
                        val host = hostPath(binds, root.path, abs)
                        if (File(host).exists()) addBind(Bind(host, dest)) else Log.w(TAG, "symlink $dest -> $target: nothing there; left out")
                    }
                }
            }
        }
        if (binds.any { it.guest == "/proc" }) genericCpuinfo(context)?.let { addBind(Bind(it.path, "/proc/cpuinfo")) }
        val argv = request.getJSONArray("argv").let { a -> (0 until a.length()).map { a.getString(it) } }
        val env = LinkedHashMap<String, String>()
        request.optJSONObject("env")?.let { e -> e.keys().forEach { k -> env[k] = e.getString(k) } }
        val x86 = LinuxFex.isX86(LinuxFex.elfMachine(File(hostPath(binds, root.path, "/usr/bin/env"))))
        if (x86) {
            fex(rootfs, dir, env)?.forEach { addBind(it) }
                ?: throw IOException("this app is built for x86-64 and the Linux runtime has no FEX to run it")
            // FEXServer needs /proc (to find itself) and a writable /tmp, and Flatpak's ldconfig sandbox has no /tmp.
            for (path in listOf("/proc", "/dev")) if (binds.none { it.guest == path }) bindsFor(session, rootfs, path, path).forEach { addBind(it) }
            if (!File(hostPath(binds, root.path, "/tmp")).isDirectory) addBind(Bind(newScratch().apply { mkdirs(); chmod(this, 0x3ff) }.path, "/tmp"))
            fakeEvdev(session, rootfs, env).forEach { addBind(it) }
        } else {
            if (binds.any { it.guest == "/dev" }) gpu(rootfs, root, env)?.forEach { addBind(it) }
            preload(session, rootfs, env).forEach { addBind(it) }
        }
        browserSandboxes(env)
        return Plan(root, binds, request.optString("cwd", "/").ifEmpty { "/" }, argv, env,
                    if (x86) listOf("$FEX_DIR/bin/FEX") else emptyList())
    }

    private const val FEX_DIR = "/run/droiddeck-fex"
    private val FEX_HOST_LIBS = listOf("ld-linux-aarch64.so.1", "libc.so.6", "libm.so.6", "libstdc++.so.6", "libgcc_s.so.1")
    private val FEX_PRELOADS = listOf("libblsession.so", "libfakeinput.so")

    // An x86-64 runtime starts under the rootfs's FEX. The sandbox's own tree is the x86 root, so FEX needs no RootFS.
    internal fun fex(rootfs: String, dir: File, env: MutableMap<String, String>): List<Bind>? {
        val bin = File(rootfs, "usr/local/bin")
        val fex = File(bin, "FEX")
        val server = File(bin, "FEXServer")
        if (!fex.isFile || !server.isFile) return null
        val binds = arrayListOf(Bind(fex.path, "$FEX_DIR/bin/FEX"), Bind(server.path, "$FEX_DIR/bin/FEXServer"))
        // FEXServer has no RUNPATH, so the ARM64 libraries go where the ARM64 loader looks by default. x86 programs
        // search /usr/lib/x86_64-linux-gnu first and skip an ARM64 library they meet.
        for (name in FEX_HOST_LIBS + "libxxhash.so.0") {
            val lib = listOf(File(rootfs, "usr/lib/$name"), File(rootfs, "usr/local/lib/$name")).firstOrNull { it.exists() }
                ?: run { Log.w(TAG, "fex: the runtime has no $name"); return null }
            binds.add(Bind(lib.canonicalPath, "/usr/lib/$name"))
        }
        // One ld.so.preload serves both: $LIB is lib for the ARM64 loader and lib/x86_64-linux-gnu (or i386) for the
        // runtime's. libblsession turns FEX's execveat of /proc/self/exe into the execve proot supports.
        val listed = FileUtils.readString(File(rootfs, "etc/ld.so.preload")) ?: ""
        val lines = ArrayList<String>()
        for (name in FEX_PRELOADS) {
            val builds = listOf(
                File(rootfs, "usr/local/lib/$name") to "lib",
                File(rootfs, "usr/local/lib/droiddeck-fex/x86_64/$name") to "lib/x86_64-linux-gnu",
                File(rootfs, "usr/local/lib/droiddeck-fex/i386/$name") to "lib/i386-linux-gnu",
            )
            if (name !in listed || builds.any { !it.first.isFile }) continue
            builds.forEach { (lib, sub) -> binds.add(Bind(lib.path, "$FEX_DIR/$sub/$name")) }
            lines.add("$FEX_DIR/\$LIB/$name")
        }
        if ("libblsession.so" !in lines.joinToString()) Log.w(TAG, "fex: no libblsession; x86 programs cannot start others")
        val preload = File(dir, "ld.so.preload").apply { writeText(lines.joinToString("\n", postfix = "\n")) }
        binds.add(Bind(preload.path, "/etc/ld.so.preload"))
        val config = File(dir, "fex-config").apply { mkdirs() }
        val cache = File(rootfs, "root/.cache/droiddeck-fex-flatpak").apply { mkdirs() }
        binds.add(Bind(config.path, "$FEX_DIR/config"))
        binds.add(Bind(cache.path, "$FEX_DIR/cache"))
        env["FEX_APP_CONFIG_LOCATION"] = "$FEX_DIR/config/"
        env["FEX_APP_DATA_LOCATION"] = "$FEX_DIR/config/"
        env["FEX_APP_CACHE_LOCATION"] = "$FEX_DIR/cache/"
        // Each sandbox gets its own server: the session's x86 Steam has one on the default socket, set up for its RootFS.
        env["FEX_SERVERSOCKETPATH"] = "droiddeck-bwrap-${dir.name}-${android.os.SystemClock.elapsedRealtime()}.FEXServer.Socket"
        // The session's tunables are for the ARM64 libc; an x86 ld.so copying them under FEX fails in sbrk.
        env.remove("GLIBC_TUNABLES")
        return binds
    }

    private const val GPU_DIR = "/run/droiddeck-gpu"

    private fun gpu(rootfs: String, root: File, env: MutableMap<String, String>): List<Bind>? {
        val lib = File(rootfs, "usr/lib")
        val driver = File(lib, "libvulkan_freedreno.so")
        if (!driver.isFile || !File("/dev/kgsl-3d0").exists()) return null
        if (env.containsKey("VK_ICD_FILENAMES") || env.containsKey("VK_DRIVER_FILES")) return null
        if (env["BL_DESKTOP"] == "1" && env["GAMESCOPE_WAYLAND_DISPLAY"] == null) return null
        val binds = ArrayList<Bind>()
        for (name in listOf("libvulkan_freedreno.so", "libdisplay-info.so.3", "libSPIRV-Tools.so", "libSPIRV-Tools-opt.so")) {
            val f = File(lib, name)
            if (!f.exists()) { Log.w(TAG, "gpu: the runtime has no $name; apps draw on the CPU"); return null }
            binds.add(Bind(f.canonicalPath, "$GPU_DIR/$name"))
        }
        val icd = File(root.parentFile, "freedreno_icd.json")
        icd.writeText("{\"file_format_version\": \"1.0.0\", \"ICD\": {\"library_path\": \"$GPU_DIR/libvulkan_freedreno.so\", \"api_version\": \"1.4.0\"}}\n")
        binds.add(Bind(icd.path, "$GPU_DIR/freedreno_icd.json"))
        env["VK_DRIVER_FILES"] = "$GPU_DIR/freedreno_icd.json"
        env["VK_ICD_FILENAMES"] = "$GPU_DIR/freedreno_icd.json"
        if (!env.containsKey("MESA_LOADER_DRIVER_OVERRIDE") && !env.containsKey("GALLIUM_DRIVER")) {
            env["MESA_LOADER_DRIVER_OVERRIDE"] = "zink"
            env["GALLIUM_DRIVER"] = "zink"
        }
        env["LD_LIBRARY_PATH"] = listOf(env["LD_LIBRARY_PATH"], GPU_DIR).filter { !it.isNullOrEmpty() }.joinToString(":")
        return binds
    }

    private fun preload(session: List<Bind>, rootfs: String, env: MutableMap<String, String>): List<Bind> {
        val libs = FileUtils.readString(File(rootfs, "etc/ld.so.preload"))?.lines()
            ?.map { it.trim() }?.filter { it.startsWith("/") && File(rootfs + it).isFile } ?: return emptyList()
        if (libs.isEmpty()) return emptyList()
        val binds = ArrayList<Bind>()
        val paths = libs.map { lib -> "$PRELOAD_DIR/${lib.substringAfterLast('/')}".also { binds.add(Bind(rootfs + lib, it)) } }
        env["LD_PRELOAD"] = (paths + listOfNotNull(env["LD_PRELOAD"]?.takeIf { it.isNotBlank() })).joinToString(":")
        return binds + fakeEvdev(session, rootfs, env)
    }

    private fun fakeEvdev(session: List<Bind>, rootfs: String, env: Map<String, String>): List<Bind> =
        env["FAKE_EVDEV_DIR"]?.let { File(it).parent }?.let { dev -> bindsFor(session, rootfs, dev, dev) } ?: emptyList()

    private const val PRELOAD_DIR = "/run/droiddeck-preload"

    private fun browserSandboxes(env: MutableMap<String, String>) {
        env.putIfAbsent("ZYPAK_ZYGOTE_STRATEGY_SPAWN", "0")
        env.putIfAbsent("QTWEBENGINE_DISABLE_SANDBOX", "1")
        if (env.containsKey("DISPLAY")) env["XDG_SESSION_TYPE"] = "x11"
        for (name in listOf("MOZ_DISABLE_CONTENT_SANDBOX", "MOZ_DISABLE_GMP_SANDBOX", "MOZ_DISABLE_RDD_SANDBOX",
                            "MOZ_DISABLE_SOCKET_PROCESS_SANDBOX", "MOZ_DISABLE_UTILITY_SANDBOX",
                            "MOZ_SHM_NO_SEALS")) {
            env.putIfAbsent(name, "1")
        }
    }

    private fun genericCpuinfo(context: Context): File? = try {
        val out = File(LinuxRuntime.liveDir(context), "cpuinfo-generic")
        val text = File("/proc/cpuinfo").readLines()
            .filterNot { it.startsWith("CPU implementer") || it.startsWith("CPU part") || it.startsWith("CPU variant") || it.startsWith("CPU revision") }
            .joinToString("\n", postfix = "\n")
        if (!out.isFile || out.readText() != text) {
            val staged = File.createTempFile("cpuinfo", null, LinuxRuntime.liveDir(context))
            staged.writeText(text)
            if (!staged.renameTo(out)) staged.delete()
        }
        out
    } catch (e: Exception) {
        Log.w(TAG, "cpuinfo", e); null
    }

    internal fun normalize(path: String): String {
        val out = ArrayList<String>()
        for (part in path.split('/')) when (part) {
            "", "." -> {}
            ".." -> if (out.isNotEmpty()) out.removeAt(out.size - 1)
            else -> out.add(part)
        }
        return "/" + out.joinToString("/")
    }

    private fun inRoot(root: File, guest: String): File? {
        var f = root
        for (part in guest.split('/').filter { it.isNotEmpty() }) {
            if (part == "..") return null
            f = File(f, part)
            if (Files.isSymbolicLink(f.toPath())) return null
        }
        return f
    }

    private fun chmod(f: File, mode: Int) {
        try { Os.chmod(f.path, mode) } catch (e: Exception) {   }
    }

    private fun run(context: Context, plan: Plan, client: LocalSocket, out: DataOutputStream, id: Int) {
        val cmd = LinuxRuntime.prootPrefix(context, plan.root, plan.cwd)
        val verbose = plan.env["DROIDDECK_PROOT_VERBOSE"]?.toIntOrNull()
        if (verbose != null) { cmd.add(1, "-v"); cmd.add(2, verbose.toString()) }
        plan.binds.forEach { cmd.add("-b"); cmd.add(it.spec) }
        cmd.addAll(plan.launcher)
        cmd.add("/usr/bin/env")
        cmd.add("-i")
        plan.env.forEach { (k, v) -> cmd.add("$k=$v") }
        cmd.addAll(plan.argv)
        val builder = ProcessBuilder(cmd).redirectErrorStream(true).directory(plan.root)
        builder.environment().apply {
            clear()
            put("PROOT_LOADER", LinuxRuntime.prootLoader(context).path)
            put("PROOT_TMP_DIR", LinuxRuntime.prootTempDir(context).path)
            if (SessionPrefs.prootNoSeccomp(context)) put("PROOT_NO_SECCOMP", "1")
            LinuxRuntime.prootLibraryPath(context).takeIf { it.isNotEmpty() }?.let { put("LD_LIBRARY_PATH", it) }
            // A launcher (FEX) reads its own settings from the environment proot hands it, not from env -i's.
            if (plan.launcher.isNotEmpty()) plan.env.filterKeys { it.startsWith("FEX_") || it == "HOME" }.forEach { (k, v) -> put(k, v) }
        }
        val process = builder.start()
        val pid = HostProcess.pidOf(process)
        com.droiddeck.launcher.session.OrphanReaper.keep(pid)
        Log.i(TAG, "sandbox $id pid $pid: ${plan.argv.joinToString(" ")} (${plan.binds.size} binds)")
        synchronized(out) { frame(out, 'P', pid.toString().toByteArray()) }
        Thread({
            try { while (client.inputStream.read() >= 0) {   } }
            catch (e: IOException) {   }
            if (process.isAlive) {
                Log.i(TAG, "sandbox $id: its bwrap is gone; stopping it")
                process.destroy()
            }
        }, "bwrap-$id-watch").apply { isDaemon = true; start() }
        val buffer = ByteArray(16384)
        var logged = 0
        process.inputStream.use { stream ->
            while (true) {
                val n = try { stream.read(buffer) } catch (e: IOException) { -1 }
                if (n < 0) break
                if (logged < 4096 || verbose != null) {
                    String(buffer, 0, if (verbose != null) n else minOf(n, 4096 - logged)).lines().filter { it.isNotBlank() }
                        .forEach { Log.i(TAG, "sandbox $id: $it") }
                    logged += n
                }
                try { synchronized(out) { frame(out, 'O', buffer.copyOf(n)) } } catch (e: IOException) { break }
            }
        }
        val status = process.waitFor()
        com.droiddeck.launcher.session.OrphanReaper.release(pid)
        Log.i(TAG, "sandbox $id exited $status")
        runCatching { synchronized(out) { frame(out, 'X', status.toString().toByteArray()) } }
    }

    private fun frame(out: DataOutputStream, kind: Char, payload: ByteArray) {
        out.writeByte(kind.code)
        out.writeInt(payload.size)
        out.write(payload)
        out.flush()
    }
}
