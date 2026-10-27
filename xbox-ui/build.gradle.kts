@file:OptIn(org.jetbrains.kotlin.gradle.ExperimentalWasmDsl::class)

plugins {
    kotlin("multiplatform") version "2.2.20"
    id("org.jetbrains.compose") version "1.9.3"
    id("org.jetbrains.kotlin.plugin.compose") version "2.2.20"
}

repositories {
    mavenCentral()
    google()
}

kotlin {
    wasmJs {
        browser {
            commonWebpackConfig {
                outputFileName = "xbox-ui.js"
            }
        }
        binaries.executable()
    }

    sourceSets {
        val wasmJsMain by getting {
            dependencies {
                implementation(compose.runtime)
                implementation(compose.foundation)
                implementation(compose.material3)
                implementation(compose.ui)
                implementation(compose.components.resources)
            }
            kotlin.srcDirs("src/wasmJsMain/kotlin", "../app/src/main/java")
            kotlin.include(
                "**/Main.kt",
                "**/R.kt",
                "**/StringResource.kt",
                "**/MathAdapter.kt",
                "**/MotionAdapter.kt",
                "**/ThemeAdapter.kt",
                "**/Icons.kt",
                "**/FilledIcons.kt",
                "**/WasmContext.kt",
                "**/WasmLocalContext.kt",
                "**/LocalLifecycleOwner.kt",
                "**/KeyEvent.kt",
                "**/BackHandler.kt",
                "**/SystemClock.kt",
                "**/WasmFile.kt",
                "**/WasmCoilCompose.kt",
                "**/WasmImageRequest.kt",
                "**/WasmScale.kt",
                "**/WasmRoundedCornersTransformation.kt",
                "**/WasmLibrary.kt",
                "**/WasmSteamFeatured.kt",
                "**/WasmSessionService.kt",
                "**/WasmGlasses.kt",
                "**/FrontEndModels.kt",
                "**/FrontEndAdapters.kt",
                "**/EmptyLibrary.kt",
                "**/Throbber.kt",
                "**/FitText.kt",
                "**/Wordmark.kt",
                "**/SharedMotion.kt",
                "**/DropSheet.kt",
                "**/LiftSheet.kt",
                "**/SteamHome.kt"
            )
        }
    }
}

tasks.withType<Copy>().configureEach {
    duplicatesStrategy = DuplicatesStrategy.EXCLUDE
}
