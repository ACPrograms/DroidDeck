package com.droiddeck.launcher.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import com.droiddeck.launcher.R
import com.droiddeck.launcher.core.AddonPack
import com.droiddeck.launcher.core.FileUtils
import com.droiddeck.launcher.runtime.VrPack
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@Composable
internal fun VrModuleRows(uninstall: Boolean?, track: (String) -> Modifier = { Modifier }) {
    val context = LocalContext.current
    val pack by VrPack.state.collectAsState()
    val removal by VrPack.removal.collectAsState()
    val title = stringResource(R.string.vr_module_title)
    val now = pack
    when {
        removal.busy -> {
            SettingsRow(title, stringResource(R.string.vr_module_removing)) {}
            Box(Modifier.fillMaxWidth().padding(start = 14.dp, end = 14.dp, bottom = 10.dp)) {
                LinearProgressIndicator(modifier = Modifier.fillMaxWidth().height(4.dp))
            }
        }
        now is AddonPack.State.Downloading -> {
            val percent = (now.progress * 100).toInt()
            SettingsRow(title, stringResource(R.string.setup_runtime_progress, stringResource(R.string.vr_module_downloading), percent)) {}
            Box(Modifier.fillMaxWidth().padding(start = 14.dp, end = 14.dp, bottom = 10.dp)) {
                LinearProgressIndicator(progress = { now.progress }, modifier = Modifier.fillMaxWidth().height(4.dp))
            }
        }
        now == AddonPack.State.Ready -> {
            val size by produceState<Long?>(null) { value = withContext(Dispatchers.IO) { VrPack.installedBytes(context) } }
            SettingsRow(title, stringResource(R.string.vr_module_installed, size?.let { FileUtils.sizeToString(it) } ?: "…")) {
                if (uninstall != null) UninstallButton(stringResource(R.string.vr_module_uninstall_title), stringResource(R.string.vr_module_uninstall_text),
                    enabled = uninstall, modifier = Modifier.testTag("vr-module-uninstall")) { VrPack.uninstall(context) }
            }
        }
        else -> {
            val hint = if (now == AddonPack.State.Failed) stringResource(R.string.vr_module_failed)
            else stringResource(R.string.vr_module_hint, FileUtils.sizeToString(VrPack.size))
            SettingsRow(title, hint) {
                PrimaryButton(stringResource(R.string.common_download), compact = true,
                    modifier = track("vr-module").testTag("vr-module-download")) { VrPack.ensure(context) }
            }
        }
    }
    removal.error?.let { Box(Modifier.padding(horizontal = 14.dp, vertical = 6.dp)) { Note(it) } }
}
