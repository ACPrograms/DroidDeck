package com.droiddeck.launcher

import android.app.Activity
import android.content.Intent
import android.os.Bundle

class LauncherDark : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        startActivity(Intent(intent).setClass(this, MainActivity::class.java)
            .setFlags(intent.flags and (Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_MULTIPLE_TASK).inv()))
        finish()
    }
}
