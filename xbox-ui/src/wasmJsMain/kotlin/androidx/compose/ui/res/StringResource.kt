package androidx.compose.ui.res

import androidx.compose.runtime.Composable
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.painter.Painter
import com.droiddeck.launcher.R

object EmptyPainter : Painter() {
    override val intrinsicSize: Size = Size.Unspecified
    override fun DrawScope.onDraw() {}
}

@Composable
fun stringResource(id: Int): String = R.resolveString(id)

@Composable
fun stringResource(id: Int, vararg formatArgs: Any): String = R.resolveString(id, *formatArgs)

@Composable
fun painterResource(id: Int): Painter = EmptyPainter
