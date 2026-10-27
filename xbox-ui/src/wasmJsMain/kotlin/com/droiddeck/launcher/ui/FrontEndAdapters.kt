package com.droiddeck.launcher.ui

import android.view.KeyEvent
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.hoverable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsFocusedAsState
import androidx.compose.foundation.interaction.collectIsHoveredAsState
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalTextStyle
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusProperties
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.key.onPreviewKeyEvent
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.droiddeck.launcher.R
import kotlin.math.abs
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers

val Any.keyCode: Int
    get() = android.view.extractKeyCode(this as? kotlin.js.JsAny)

val Any.action: Int
    get() = android.view.extractAction(this as? kotlin.js.JsAny)

val Any.repeatCount: Int
    get() = android.view.extractRepeatCount(this as? kotlin.js.JsAny)

val Dispatchers.IO: CoroutineDispatcher
    get() = Dispatchers.Default

class Glasses(
    val present: Boolean = false,
    val canControl: Boolean = false,
    val usable: Boolean = false,
)

class GlassesSettings

object GlassesTab {
    const val DISPLAY = "display"
    const val VR = "vr"
    const val TRACKING = "tracking"
    const val HARDWARE = "hardware"
    const val CONTROLLERS = "controllers"
}

@Composable
fun rememberGlasses(): Pair<Glasses, GlassesSettings> =
    remember { Pair(Glasses(), GlassesSettings()) }

fun glassesTabs(glasses: Glasses): List<Pair<String, String>> = emptyList()

@Composable
fun VrControllerGroups(host: Any?) {}

@Composable
fun GlassesGroups(
    host: Any?,
    glasses: Glasses,
    settings: GlassesSettings,
    onRecenter: () -> Unit,
    inSession: Boolean,
    tab: String,
) {}

@Composable
fun rememberMenuHost(): Any? = null

internal val Shape12 = RoundedCornerShape(12.dp)

internal fun hueOf(name: String): Float = (name.hashCode().toUInt() % 360u).toFloat()

internal fun tint(h: Float, s: Float = 0.7f, v: Float = 0.58f): Color {
    val hue = ((h % 360f) + 360f) % 360f
    val c = v * s
    val x = c * (1f - abs((hue / 60f) % 2f - 1f))
    val m = v - c
    val (r1, g1, b1) = when {
        hue < 60f -> Triple(c, x, 0f)
        hue < 120f -> Triple(x, c, 0f)
        hue < 180f -> Triple(0f, c, x)
        hue < 240f -> Triple(0f, x, c)
        hue < 300f -> Triple(x, 0f, c)
        else -> Triple(c, 0f, x)
    }
    return Color(r1 + m, g1 + m, b1 + m, 1f)
}

internal fun artBrush(h: Float): Brush = Brush.linearGradient(
    listOf(
        tint(h),
        tint((h + 32f) % 360f, 0.65f, 0.30f),
        tint((h + 64f) % 360f, 0.6f, 0.14f),
    )
)

@Composable
internal fun Chip(t: String, ok: Boolean) {
    val colors = MaterialTheme.colorScheme
    val pal = LocalPalette.current
    Text(
        t,
        fontSize = 12.sp,
        color = if (ok) pal.good else colors.onSurfaceVariant,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier
            .clip(RoundedCornerShape(99.dp))
            .background(colors.surfaceVariant)
            .border(1.dp, if (ok) pal.good.copy(alpha = 0.3f) else pal.line, RoundedCornerShape(99.dp))
            .padding(horizontal = 9.dp, vertical = 4.dp),
    )
}

@Composable
internal fun RuntimeChip(s: FrontEndState) = when {
    s.busy -> Chip(if (s.percent >= 0) stringResource(R.string.setup_runtime_progress, s.stage, s.percent) else s.stage, ok = false)
    s.removalPending -> Chip(stringResource(R.string.runtime_removal_incomplete), ok = false)
    !s.ready -> Chip(stringResource(R.string.games_runtime_first_play), ok = false)
    s.available != null && s.available != s.installed -> Chip(stringResource(R.string.games_runtime_update), ok = false)
    else -> Chip(stringResource(R.string.games_runtime_ready), ok = true)
}

internal fun Modifier.controllerConfirm(enabled: Boolean = true, onClick: () -> Unit): Modifier = onPreviewKeyEvent { event ->
    val keyEvent = event.nativeKeyEvent
    if (keyEvent.keyCode != KeyEvent.KEYCODE_BUTTON_A) {
        false
    } else {
        if (enabled && keyEvent.action == KeyEvent.ACTION_DOWN && keyEvent.repeatCount == 0) onClick()
        true
    }
}

internal fun Modifier.controllerBack(onBack: () -> Unit): Modifier = onPreviewKeyEvent { event ->
    val keyEvent = event.nativeKeyEvent
    if (keyEvent.keyCode != KeyEvent.KEYCODE_BUTTON_B && keyEvent.keyCode != KeyEvent.KEYCODE_BACK) {
        false
    } else {
        if (keyEvent.action == KeyEvent.ACTION_DOWN && keyEvent.repeatCount == 0) onBack()
        true
    }
}

internal fun Modifier.bumpers(onPrevious: () -> Unit, onNext: () -> Unit): Modifier = onPreviewKeyEvent { event ->
    val keyEvent = event.nativeKeyEvent
    val previous = keyEvent.keyCode == KeyEvent.KEYCODE_BUTTON_L1
    if (!previous && keyEvent.keyCode != KeyEvent.KEYCODE_BUTTON_R1) {
        false
    } else {
        if (keyEvent.action == KeyEvent.ACTION_DOWN && keyEvent.repeatCount == 0) {
            if (previous) onPrevious() else onNext()
        }
        true
    }
}

@Composable
internal fun Modifier.glideBorder(
    hot: Boolean,
    shape: Shape,
    hotColor: Color,
    restColor: Color = Color.Transparent,
    hotWidth: Dp = 2.dp,
    restWidth: Dp = 1.dp,
    hotFillAlpha: Float = 0.18f,
): Modifier {
    return border(if (hot) hotWidth else restWidth, if (hot) hotColor else restColor, shape)
}

@Composable
internal fun Modifier.downToFirstTile(): Modifier {
    if (LocalSheetBeat.current != null) return this
    val ff = LocalFrontFocus.current ?: return this
    return this.focusProperties { down = if (ff.firstTileAttached > 0) ff.firstTile else FocusRequester.Default }
}

@Composable
internal fun Modifier.paneItem(id: String): Modifier {
    val ff = LocalFrontFocus.current ?: return this
    val req = remember(id) { ff.items.getOrPut(id) { FocusRequester() } }
    DisposableEffect(id) {
        ff.attached[id] = (ff.attached[id] ?: 0) + 1
        onDispose { ff.attached[id] = (ff.attached[id] ?: 1) - 1 }
    }
    return this.focusRequester(req).onFocusChanged { if (it.isFocused) ff.last = id }
}

@Composable
internal fun VeilRing() {}

internal class HopOrigin(
    val rail: String,
    val surface: String,
    val tab: String?,
    val control: String,
    val bounds: Rect,
    val back: String,
    val dest: String,
    val page: String,
)

internal const val HOP_SURFACE_GAMES = "games"
internal const val HOP_SURFACE_DESKTOP = "desktop"
internal const val HOP_SURFACE_SHEET = "sheet"

internal object Hops {
    var restore: HopOrigin? = null
    val sources = HashMap<String, Rect>()
}

internal fun Modifier.hopSource(key: String): Modifier = onGloballyPositioned { Hops.sources[key] = it.boundsInRoot() }

internal object LaunchOrigin {
    var flooding: Any? = null
    fun mark(r: Rect, key: Any) {}
    fun take(): Rect? = null
    fun takeReturn(): Rect? = null
}

@Composable
internal fun rememberHot(src: MutableInteractionSource): Boolean =
    src.collectIsFocusedAsState().value || src.collectIsHoveredAsState().value

@Composable
internal fun PrimaryButton(
    text: String,
    enabled: Boolean = true,
    main: Boolean = false,
    compact: Boolean = false,
    modifier: Modifier = Modifier,
    large: Boolean = false,
    icon: ImageVector? = null,
    fill: Boolean = false,
    onClick: () -> Unit,
) {
    val colors = MaterialTheme.colorScheme
    val pal = LocalPalette.current
    val frontFocus = if (main) LocalFrontFocus.current else null
    if (frontFocus != null) DisposableEffect(Unit) {
        frontFocus.primaryAttached++
        onDispose { frontFocus.primaryAttached-- }
    }
    val track = (if (frontFocus == null) Modifier.paneItem("btn:$text") else Modifier).downToFirstTile()
    val src = remember { MutableInteractionSource() }
    val hot = rememberHot(src) && enabled
    val pressed by src.collectIsPressedAsState()
    val scale by animateFloatAsState(
        if (pressed) 0.955f else if (hot) 1.02f else 1f,
        Motion.sp(0.5f, Spring.StiffnessMedium),
        label = "btnScale",
    )
    val lift by animateFloatAsState(
        if (hot) 14f else 6f,
        Motion.tw(300),
        label = "btnLift",
    )
    val placed = remember { arrayOfNulls<androidx.compose.ui.layout.LayoutCoordinates>(1) }
    Row(
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = if (fill) Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally) else Arrangement.spacedBy(8.dp),
        modifier = modifier.then(track)
            .onGloballyPositioned { placed[0] = it }
            .heightIn(min = if (compact) 44.dp else 48.dp)
            .then(if (frontFocus != null) Modifier.focusRequester(frontFocus.primary).onFocusChanged { if (it.isFocused) frontFocus.last = FrontFocus.PRIMARY } else Modifier)
            .graphicsLayer {
                alpha = if (LaunchOrigin.flooding === placed) 0f else 1f
                scaleX = scale
                scaleY = scale
                shadowElevation = if (enabled) lift.dp.toPx() else 0f
                shape = Shape12
                clip = false
                ambientShadowColor = pal.signal
                spotShadowColor = pal.signal
            }
            .clip(Shape12)
            .background(if (enabled) Brush.linearGradient(listOf(colors.primary, pal.primary2)) else Brush.linearGradient(listOf(colors.surfaceVariant, colors.surfaceVariant)))
            .then(if (enabled) Modifier else Modifier.border(1.dp, pal.line2, Shape12))
            .glideBorder(hot, Shape12, pal.signal, hotFillAlpha = 0f)
            .hoverable(src)
            .clickable(role = Role.Button, interactionSource = src, indication = null, enabled = enabled) {
                placed[0]?.takeIf { it.isAttached }?.let { LaunchOrigin.mark(it.boundsInRoot(), placed) }
                onClick()
            }
            .padding(
                start = if (fill) 12.dp else if (large) 20.dp else if (compact) 10.dp else if (icon != null) 14.dp else 18.dp,
                end = if (fill) 12.dp else if (large) 26.dp else if (compact) 10.dp else 18.dp,
                top = if (large) 16.dp else if (compact) 7.dp else 11.dp,
                bottom = if (large) 16.dp else if (compact) 7.dp else 11.dp,
            ),
    ) {
        val fg = if (enabled) colors.onPrimary else colors.onSurfaceVariant
        if (icon != null) Icon(icon, contentDescription = null, tint = fg, modifier = Modifier.size(if (large) 20.dp else 17.dp))
        val style = LocalTextStyle.current.merge(
            TextStyle(
                fontSize = if (large) 17.sp else if (compact) 12.sp else 15.sp,
                fontWeight = if (large) FontWeight.Bold else FontWeight.SemiBold,
                letterSpacing = 0.5.sp,
                color = fg,
            ),
        )
        if (fill) FittedLabel(text, style, Modifier.weight(1f, fill = false))
        else Text(text, style = style, maxLines = 1)
    }
}

@Composable
private fun FittedLabel(text: String, style: TextStyle, modifier: Modifier = Modifier) {
    BoxWithConstraints(modifier) {
        val measurer = rememberTextMeasurer()
        val max = constraints.maxWidth
        val size = remember(text, style, max) {
            var size = style.fontSize
            while (size.value > 10f && measurer.measure(text, style.copy(fontSize = size), maxLines = 1, softWrap = false).size.width > max) {
                size = (size.value - 0.5f).sp
            }
            size
        }
        Text(text, style = style.copy(fontSize = size), maxLines = 1, softWrap = false)
    }
}
