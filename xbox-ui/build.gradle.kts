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
                "**/MathAdapter.kt",
                "**/MotionAdapter.kt",
                "**/ThemeAdapter.kt",
                "**/EmptyLibrary.kt",
                "**/Throbber.kt",
                "**/FitText.kt"
            )
        }
    }
}

tasks.withType<Copy>().configureEach {
    duplicatesStrategy = DuplicatesStrategy.EXCLUDE
}
