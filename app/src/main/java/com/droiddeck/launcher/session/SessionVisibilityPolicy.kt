package com.droiddeck.launcher.session

internal object SessionVisibilityPolicy {
    fun isHidden(
        activityStarted: Boolean,
        screenOn: Boolean,
        activityOnDefaultDisplay: Boolean,
        activityDisplayOn: Boolean,
        xrActivityVisible: Boolean = false,
    ): Boolean = !sessionHasVisibleSurface(
        panelVisible = activityStarted,
        screenOn = if (activityOnDefaultDisplay) screenOn else activityDisplayOn,
        xrVisible = xrActivityVisible,
    )

    fun shouldResumeOnVisibleTransition(
        previouslyHidden: Boolean?,
        currentlyHidden: Boolean,
        suspendPolicy: String,
    ): Boolean = previouslyHidden == true && !currentlyHidden &&
        suspendPolicy in setOf(SessionPrefs.SUSPEND_AUTO, SessionPrefs.SUSPEND_NATIVE)
}
