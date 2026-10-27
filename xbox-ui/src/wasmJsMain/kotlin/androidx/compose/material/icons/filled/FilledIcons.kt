package androidx.compose.material.icons.filled

import androidx.compose.material.icons.Icons
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.path
import androidx.compose.ui.unit.dp

val Icons.Filled.PlayArrow: ImageVector by lazy {
    ImageVector.Builder(
        name = "Filled.PlayArrow",
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f,
    ).apply {
        path(fill = SolidColor(Color.White)) {
            moveTo(8.0f, 5.0f)
            lineTo(8.0f, 19.0f)
            lineTo(19.0f, 12.0f)
            close()
        }
    }.build()
}

val Icons.Filled.Close: ImageVector by lazy {
    ImageVector.Builder(
        name = "Filled.Close",
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f,
    ).apply {
        path(fill = SolidColor(Color.White)) {
            moveTo(19.0f, 6.41f)
            lineTo(17.59f, 5.0f)
            lineTo(12.0f, 10.59f)
            lineTo(6.41f, 5.0f)
            lineTo(5.0f, 6.41f)
            lineTo(10.59f, 12.0f)
            lineTo(5.0f, 17.59f)
            lineTo(6.41f, 19.0f)
            lineTo(12.0f, 13.41f)
            lineTo(17.59f, 19.0f)
            lineTo(19.0f, 17.59f)
            lineTo(13.41f, 12.0f)
            close()
        }
    }.build()
}

val Icons.Filled.KeyboardArrowDown: ImageVector by lazy {
    ImageVector.Builder(
        name = "Filled.KeyboardArrowDown",
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f,
    ).apply {
        path(fill = SolidColor(Color.White)) {
            moveTo(7.41f, 8.59f)
            lineTo(12.0f, 13.17f)
            lineTo(16.59f, 8.59f)
            lineTo(18.0f, 10.0f)
            lineTo(12.0f, 16.0f)
            lineTo(6.0f, 10.0f)
            close()
        }
    }.build()
}

val Icons.Filled.KeyboardArrowUp: ImageVector by lazy {
    ImageVector.Builder(
        name = "Filled.KeyboardArrowUp",
        defaultWidth = 24.dp,
        defaultHeight = 24.dp,
        viewportWidth = 24f,
        viewportHeight = 24f,
    ).apply {
        path(fill = SolidColor(Color.White)) {
            moveTo(7.41f, 15.41f)
            lineTo(12.0f, 10.83f)
            lineTo(16.59f, 15.41f)
            lineTo(18.0f, 14.0f)
            lineTo(12.0f, 8.0f)
            lineTo(6.0f, 14.0f)
            close()
        }
    }.build()
}
