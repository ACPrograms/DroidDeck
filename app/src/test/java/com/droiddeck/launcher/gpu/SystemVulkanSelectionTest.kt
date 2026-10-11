package com.droiddeck.launcher.gpu

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.assertThrows
import org.junit.Test

class SystemVulkanSelectionTest {
    @Test fun defaultPowerVrUsesTheSystemBridge() {
        assertTrue(SystemVulkanDriver.usesDefault(true, ""))
        assertTrue(SystemVulkanDriver.usesDefault(true, null))
    }

    @Test fun otherGpusAndExplicitImportsKeepTheirDriver() {
        assertFalse(SystemVulkanDriver.usesDefault(false, ""))
        assertFalse(SystemVulkanDriver.usesDefault(false, null))
        assertFalse(SystemVulkanDriver.usesDefault(true, "custom-driver"))
    }
    @Test fun renderNodeRequiresARealCharacterDeviceAndBothAccessModes() {
        assertTrue(SystemVulkanDriver.renderNodeAvailable(true, true, true))
        assertFalse("a regular placeholder is not a DRM device", SystemVulkanDriver.renderNodeAvailable(false, true, true))
        assertFalse("read access is required", SystemVulkanDriver.renderNodeAvailable(true, false, true))
        assertFalse("write access is required", SystemVulkanDriver.renderNodeAvailable(true, true, false))
        assertFalse("a missing node is unavailable", SystemVulkanDriver.renderNodeAvailable(false, false, false))
    }

    @Test fun defaultPowerVrResolvesToTheSystemIcd() {
        assertEquals("system/icd.json", LinuxVulkanDriver.resolveIcdPath("", true,
            { "system/icd.json" }, { error("default must not resolve an import") }))
    }

    @Test fun defaultAdrenoLeavesTheRuntimeIcdUnchanged() {
        assertNull(LinuxVulkanDriver.resolveIcdPath("", false,
            { error("Adreno must not prepare the system bridge") }, { error("default must not resolve an import") }))
    }

    @Test fun missingManualPowerVrImportFailsWithoutSwitchingBackends() {
        val failure = assertThrows(IllegalStateException::class.java) {
            LinuxVulkanDriver.resolveIcdPath("missing-driver", true,
                { error("manual preference must not silently switch to the system backend") }, { null })
        }
        assertTrue(failure.message!!.contains("missing-driver"))
        assertTrue(failure.message!!.contains("use Auto"))
    }

    @Test fun missingAdrenoImportKeepsTheExistingRuntimeFallback() {
        assertNull(LinuxVulkanDriver.resolveIcdPath("missing-driver", false,
            { error("Adreno must not prepare the system bridge") }, { null }))
    }

    @Test fun manualPowerVrResolvesTheChosenImport() {
        assertEquals("imports/chosen/icd.json", LinuxVulkanDriver.resolveIcdPath("chosen", true,
            { error("manual must not prepare the system bridge") }, { "imports/$it/icd.json" }))
    }
}
