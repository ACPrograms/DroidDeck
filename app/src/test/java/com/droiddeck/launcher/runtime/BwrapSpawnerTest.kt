package com.droiddeck.launcher.runtime

import android.util.Base64
import org.robolectric.RuntimeEnvironment
import com.droiddeck.launcher.runtime.BwrapSpawner.Bind
import java.io.File
import java.io.IOException
import java.nio.file.Files
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class BwrapSpawnerTest {
    private val session = listOf(
        Bind("/dev", "/dev"),
        Bind("/cache/shm", "/dev/shm"),
        Bind("/proc", "/proc"),
        Bind("/rootfs/etc/droiddeck/proc/stat", "/proc/stat"),
        Bind("/data/files", "/data/files"),
    )

    @Test fun underRespectsPathBoundaries() {
        assertTrue(BwrapSpawner.under("/usr/lib", "/usr"))
        assertTrue(BwrapSpawner.under("/usr", "/usr"))
        assertFalse(BwrapSpawner.under("/usrlocal", "/usr"))
        assertTrue(BwrapSpawner.under("/anything", "/"))
    }

    @Test fun hostPathGoesThroughTheMostSpecificSessionBind() {
        assertEquals("/cache/shm/x", BwrapSpawner.hostPath(session, "/rootfs", "/dev/shm/x"))
        assertEquals("/dev/null", BwrapSpawner.hostPath(session, "/rootfs", "/dev/null"))
        assertEquals("/data/files/.wayland-rt/wayland-1", BwrapSpawner.hostPath(session, "/rootfs", "/data/files/.wayland-rt/wayland-1"))
    }

    @Test fun hostPathFallsBackToTheRootfs() {
        assertEquals("/rootfs/root/.local/share/flatpak", BwrapSpawner.hostPath(session, "/rootfs/", "/root/.local/share/flatpak"))
    }

    @Test fun bindingDevBringsTheSessionsStandInsAlong() {
        val binds = BwrapSpawner.bindsFor(session, "/rootfs", "/dev", "/dev")
        assertEquals(Bind("/dev", "/dev"), binds.first())
        assertTrue(Bind("/cache/shm", "/dev/shm") in binds)
        assertFalse(binds.any { it.guest.startsWith("/proc") })
    }

    @Test fun nestedBindsFollowARenamedDestination() {
        val binds = BwrapSpawner.bindsFor(session, "/rootfs", "/proc", "/newproc")
        assertTrue(Bind("/rootfs/etc/droiddeck/proc/stat", "/newproc/stat") in binds)
    }

    @Test fun normalizeResolvesDotsLexically() {
        assertEquals("/usr/lib/x", BwrapSpawner.normalize("/usr/lib/GL/../x"))
        assertEquals("/", BwrapSpawner.normalize("/../.."))
    }

    @Test fun bindSpecsRoundTrip() {
        assertEquals(Bind("/a", "/b"), Bind.parse("/a:/b"))
        assertEquals(Bind("/a", "/a"), Bind.parse("/a"))
        assertEquals("/a", Bind("/a", "/a").spec)
        assertEquals("/a:/b", Bind("/a", "/b").spec)
    }
}

@RunWith(RobolectricTestRunner::class)
class BwrapSpawnerPlanTest {
    private lateinit var dir: File
    private lateinit var runtime: File

    @Before fun setUp() {
        dir = Files.createTempDirectory("bwrap-plan").toFile()
        runtime = File(LinuxRuntime.rootDir(RuntimeEnvironment.getApplication()), "runtime-files")
        File(runtime, "lib").mkdirs()
    }

    @After fun tearDown() {
        dir.deleteRecursively()
        runtime.deleteRecursively()
    }

    private fun op(vararg pairs: Pair<String, Any>) = JSONObject().apply { pairs.forEach { (k, v) -> put(k, v) } }

    private fun request(vararg ops: JSONObject) = JSONObject()
        .put("argv", JSONArray().put("/app/bin/game"))
        .put("env", JSONObject().put("FLATPAK_ID", "org.example.Game"))
        .put("cwd", "/app")
        .put("ops", JSONArray().apply { ops.forEach { put(it) } })

    @Test fun buildsTheTreeFlatpakAsksFor() {
        val context = RuntimeEnvironment.getApplication()
        val info = Base64.encodeToString("[Application]\nname=org.example.Game\n".toByteArray(), Base64.NO_WRAP)
        val plan = BwrapSpawner.plan(context, request(
            op("t" to "bind", "src" to "/runtime-files", "dest" to "/usr", "ro" to true),
            op("t" to "symlink", "target" to "usr/lib", "dest" to "/lib"),
            op("t" to "dir", "dest" to "/usr/lib/GL", "empty" to true),
            op("t" to "symlink", "target" to "default/lib/libEGL.so", "dest" to "/usr/lib/GL/libEGL.so"),
            op("t" to "dir", "dest" to "/tmp"),
            op("t" to "file", "dest" to "/.flatpak-info", "data" to info, "perms" to 0x180),
            op("t" to "bind", "src" to "/nonexistent-${System.nanoTime()}", "dest" to "/run/x", "try" to true),
        ), dir)

        assertTrue(plan.binds.any { it.guest == "/usr" && File(it.host).canonicalPath == runtime.canonicalPath })
        assertFalse(File(runtime, "lib/GL").exists())
        val gl = plan.binds.single { it.guest == "/usr/lib/GL" }
        assertTrue(gl.host.startsWith(dir.path))
        assertEquals("default/lib/libEGL.so", Files.readSymbolicLink(File(gl.host, "libEGL.so").toPath()).toString())
        assertEquals("usr/lib", Files.readSymbolicLink(File(plan.root, "lib").toPath()).toString())
        assertTrue(File(plan.root, "tmp").isDirectory)
        assertEquals("[Application]\nname=org.example.Game\n", File(plan.root, ".flatpak-info").readText())
        assertFalse(plan.binds.any { it.guest == "/run/x" })
        assertEquals(listOf("/app/bin/game"), plan.argv)
        assertEquals("/app", plan.cwd)
        assertEquals("org.example.Game", plan.env["FLATPAK_ID"])
    }

    private fun elf(file: File, machine: Int) = file.apply {
        parentFile!!.mkdirs()
        writeBytes(ByteArray(64).also { b ->
            "\u007fELF".toByteArray().copyInto(b); b[4] = 2; b[5] = 1; b[18] = machine.toByte()
        })
    }

    private fun x86Request() = request(
        op("t" to "bind", "src" to "/runtime-files", "dest" to "/usr", "ro" to true),
        op("t" to "symlink", "target" to "usr/lib", "dest" to "/lib"),
    )

    private fun fexInRootfs(rootfs: File) {
        for (name in listOf("usr/local/bin/FEX", "usr/local/bin/FEXServer", "usr/lib/ld-linux-aarch64.so.1", "usr/lib/libc.so.6",
                            "usr/lib/libm.so.6", "usr/lib/libstdc++.so.6", "usr/lib/libgcc_s.so.1", "usr/local/lib/libxxhash.so.0")) {
            elf(File(rootfs, name), LinuxFex.ELF_AARCH64)
        }
        for (dir in listOf("usr/local/lib", "usr/local/lib/droiddeck-fex/x86_64", "usr/local/lib/droiddeck-fex/i386")) {
            for (name in listOf("libblsession.so", "libfakeinput.so")) File(rootfs, "$dir/$name").apply { parentFile!!.mkdirs(); writeText("") }
        }
        File(rootfs, "etc/ld.so.preload").apply { parentFile!!.mkdirs() }
            .writeText("/usr/local/lib/libblsession.so\n/usr/local/lib/libfakeinput.so\n/usr/local/lib/libblfastpath.so\n")
    }

    @Test fun anX86RuntimeStartsUnderFexWithPreloadsForBothArches() {
        val context = RuntimeEnvironment.getApplication()
        val rootfs = LinuxRuntime.rootDir(context)
        fexInRootfs(rootfs)
        elf(File(runtime, "bin/env"), LinuxFex.ELF_X86_64)
        try {
            val plan = BwrapSpawner.plan(context, x86Request(), dir)
            assertEquals(listOf("/run/droiddeck-fex/bin/FEX"), plan.launcher)
            assertTrue(Bind(File(rootfs, "usr/local/bin/FEXServer").path, "/run/droiddeck-fex/bin/FEXServer") in plan.binds)
            assertTrue(plan.binds.any { it.guest == "/usr/lib/ld-linux-aarch64.so.1" })
            assertTrue(plan.binds.any { it.guest == "/usr/lib/libxxhash.so.0" })
            assertTrue(plan.binds.any { it.guest == "/run/droiddeck-fex/lib/x86_64-linux-gnu/libblsession.so" })
            val preload = File(plan.binds.single { it.guest == "/etc/ld.so.preload" }.host).readText()
            assertEquals("/run/droiddeck-fex/\$LIB/libblsession.so\n/run/droiddeck-fex/\$LIB/libfakeinput.so\n", preload)
            assertNull(plan.env["LD_PRELOAD"])
            assertNull(plan.env["VK_ICD_FILENAMES"])
            assertTrue(plan.env.getValue("FEX_SERVERSOCKETPATH").endsWith(".FEXServer.Socket"))
            assertEquals("/run/droiddeck-fex/config/", plan.env["FEX_APP_CONFIG_LOCATION"])
            assertTrue(plan.binds.any { it.guest == "/proc" })
            assertTrue(plan.binds.any { it.guest == "/dev" })
            assertTrue(File(plan.binds.single { it.guest == "/tmp" }.host).isDirectory)
        } finally {
            rootfs.deleteRecursively()
        }
    }

    @Test fun anArmRuntimeIsStartedDirectly() {
        val context = RuntimeEnvironment.getApplication()
        elf(File(runtime, "bin/env"), LinuxFex.ELF_AARCH64)
        val plan = BwrapSpawner.plan(context, x86Request(), dir)
        assertEquals(emptyList<String>(), plan.launcher)
        assertFalse(plan.binds.any { it.guest == "/etc/ld.so.preload" })
    }

    @Test fun anX86RuntimeWithoutFexIsRefused() {
        val context = RuntimeEnvironment.getApplication()
        elf(File(runtime, "bin/env"), LinuxFex.ELF_X86_64)
        File(LinuxRuntime.rootDir(context), "usr/local/bin/FEX").delete()
        assertThrows(IOException::class.java) { BwrapSpawner.plan(context, x86Request(), dir) }
    }
}
