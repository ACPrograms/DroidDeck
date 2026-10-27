# Original Compose UI portability gate

Build from the repository root with JDK 21:

```sh
./gradlew -p xbox-ui wasmJsBrowserDistribution
```

This standalone target compiles the original `EmptyLibrary`, `Throbber`, and
`FitText` sources directly. Platform adapters supply the original Graphite
palette, motion preference, and JVM math equivalents. The Android build is
unchanged. Browser assets appear under `build/dist/wasmJs/productionExecutable`.

`DROIDDECK_UI_READY` indicates composition reached two frame callbacks; it does
not prove presentation or Xbox rendering. Only the original empty library is
hosted. Library/services/runtime integration and actual Steam gameplay remain
unverified. This target alone is not the DroidDeck Xbox application.
