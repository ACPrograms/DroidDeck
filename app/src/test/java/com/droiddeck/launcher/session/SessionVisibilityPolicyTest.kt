package com.droiddeck.launcher.session

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SessionVisibilityPolicyTest {
    @Test fun handsetScreenLockHidesActivityOnDefaultDisplay() {
        assertFalse(SessionVisibilityPolicy.isHidden(true, true, true, true))
        assertTrue(SessionVisibilityPolicy.isHidden(true, false, true, true))
    }

    @Test fun handsetScreenOffDoesNotHideActivityOnPoweredOnDexDisplay() {
        assertFalse(SessionVisibilityPolicy.isHidden(true, false, false, true))
    }

    @Test fun externalDisplayOffHidesActivityEvenWhenHandsetIsOn() {
        assertTrue(SessionVisibilityPolicy.isHidden(true, true, false, false))
    }

    @Test fun disconnectedExternalDisplayIsHidden() {
        assertTrue(SessionVisibilityPolicy.isHidden(true, false, false, false))
    }

    @Test fun hiddenExternalActivityIsHiddenWhileItsDisplayIsOn() {
        assertTrue(SessionVisibilityPolicy.isHidden(false, false, false, true))
    }

    @Test fun startedButUnfocusedMultiwindowActivityRemainsVisible() {
        // SessionActivity reports onStart/onStop; a focus-only onPause does not mark it hidden.
        assertFalse(SessionVisibilityPolicy.isHidden(true, true, true, true))
    }
}
