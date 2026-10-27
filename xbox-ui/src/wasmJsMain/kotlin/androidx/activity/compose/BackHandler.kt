package androidx.activity.compose

import androidx.compose.runtime.Composable

@Composable
fun BackHandler(enabled: Boolean = true, onBack: () -> Unit) {
    // Platform adapter: Back actions on Xbox/Wasm are routed via Modifier.controllerBack (B button)
}
