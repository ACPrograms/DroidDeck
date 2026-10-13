package com.droiddeck.launcher.session

/**
 * The handset's interactive state is only a visibility signal for activities on its default
 * display. External-display activities follow the state of their own display instead.
 */
internal object SessionVisibilityPolicy {
    fun isHidden(
        activityStarted: Boolean,
        screenOn: Boolean,
        activityOnDefaultDisplay: Boolean,
        activityDisplayOn: Boolean,
    ): Boolean = !activityStarted || if (activityOnDefaultDisplay) !screenOn else !activityDisplayOn
}
