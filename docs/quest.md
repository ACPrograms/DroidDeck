# Experimental Quest 2/3/3S support

Quest runs DroidDeck as a 2D panel, with an experimental immersive SteamVR path
in the standard app. Quest 2 has a separate experimental Adreno 6xx driver path.

Quest 2 selects `turnip26.3.0-quest2`, a pinned Banners-Turnip A6xx/A7xx build
with the same GPUOBJ adapter. It does not select the Quest 3 A7xx binary.
The first Quest 2 launch applies manual driver selection and the SurfaceControl
workaround, even if `questDefaultsV1` exists from an earlier install. Later user
choices are retained. The separate `turnip26.3.0-a6xx` bundle provides a plain
Adreno 6xx offline fallback for other devices; their driver Auto behavior remains.
Neither family-wide hardware coverage nor Quest 2 operation has been verified.

The A6xx input URL and SHA-256 are pinned in `tools/quest/build-driver.py`.
Both A6xx bundles are prepared by the existing local/CI wrapper build step and
covered by runtime-input attestation. Quest 3/3S retains its original driver.
Early installation and compositor breadcrumbs are written to `driver-setup.txt`
in private files and, when available, the app's external files directory. They
do not require a Steam session. The existing Quest Java crash file now also
applies to Quest 2; native crashes still require Android logcat.

Install the standard DroidDeck APK on Quest 3/3S. Device detection on the
first launch selects manual drivers, the bundled
`turnip25.1.0-quest1` Android driver and the default Linux driver. It enables
`hdr.steam` to use the existing SurfaceControl presentation workaround from
[the Quest report](https://github.com/Droid-Deck/DroidDeck/issues/245).
Presentation also has a Quest-specific layer path for complete SDR scenes and
the black backdrop, independent of this HDR preference; see
[the presentation candidate and headset test](quest-presentation-test.md).
These defaults apply once; subsequent user changes are retained. Colour
accuracy and headset performance still need testing.

Selecting driver Auto also uses the bundled Quest display driver and runtime
default, without downloading a generic Adreno driver pair. Manual selections
remain available and are not replaced on startup. Android's Auto-by-GPU fallback
uses the Quest bundle too; the external `Download/droiddeck-driver` override
still takes precedence. Fully close and restart DroidDeck after changing the
display driver, since the compositor loads it once per app process.

Both the Android driver wrapper and the Linux session preload use
`tools/quest/kgsl_compat.c`. Successful legacy allocation/free calls are
unchanged. Only `EACCES`/`EPERM` failures retry through `GPUOBJ_ALLOC`,
`GPUOBJ_INFO` and `GPUOBJ_FREE`. The Android wrapper depends on the original
bundled Turnip binary. Unhandled controller ioctls forward to the next
preload hook rather than skipping it with a raw syscall.

`tools/build_local.sh` and CI build the wrapper from source using the pinned
Android NDK; the normal preload builds include the shared Linux adapter.
Runtime input verification checks the resulting driver archive and invalidates
stale prepared assets after compatibility sources change. To rebuild only the
Android driver archive:

```sh
python3 tools/quest/build-driver.py "$ANDROID_HOME/ndk/27.3.13750724"
```

Quest compatibility is included in the standard APK; no separate Quest
package is needed. Other devices retain their existing defaults. The remaining
performance-name APK variants continue to use separate app data and bwrap sockets.

Quest 3 testers have successfully launched games.

On a headset, check the Steam login panel, sign-in, game installation and
then an owned game's rendering/input. Capture the device/OS, session logs,
`files/quest-startup.txt` and any `files/quest-last-crash.txt` failure.
Host allocation and preload-chain tests do not establish headset operation.

## Experimental immersive SteamVR

In a running Steam session, open the session menu's SteamVR tab, install SteamVR
once, then choose Enter SteamVR. Cardboard has its own tab and can remain off. On supported Quest devices this opens the standard app's
OpenXR activity. The guest driver receives headset poses, Touch controller input
and display timing, and returns owned stereo images with their render poses.
SteamVR haptics return through the tracking bridge. No root or separate XR APK
is required. Other devices retain their existing SteamVR start behavior.

The left Touch menu button is SteamVR's system button and toggles its dashboard.
The right Meta button remains reserved by the headset OS.

The panel compositor detaches before OpenXR starts displaying guest frames.
Session suspension accounts for the immersive activity while the panel is hidden.
Closing the immersive activity stops SteamVR. The panel remains available for
returning to the normal Steam session.
The immersive activity belongs to the Steam session that opened it. Ending that
session closes XR; a failed startup or stopped SteamVR also returns to the panel
with an error instead of leaving a black scene. Enter SteamVR can then start a
fresh connection.

With Steam already running, `python3 tools/test_quest_xr.py --serial SERIAL`
launches the immersive activity and verifies advancing owned images, tracked
6DoF, focus and SteamVR status. Add `--require-controllers` while holding both
controllers. Results include telemetry and logcat in `build/questxr-validation`.
Host tests and APK installation alone do not establish headset operation.

On Quest 3, a 45-second hardware run maintained focused tracked 6DoF, both
controllers active, and advancing stereo images. A headset tester confirmed
controller models and trigger animation. That run delivered about 8.5 new stereo
images per second despite a 90 Hz XR session.
This branch retains the safe owned-frame transfer path; reducing transfer cost,
validating game input and testing actual VR games remain necessary.

The frame channel carries linear RGBA8. Android samples it as linear and writes
to an sRGB eye swapchain, encoding the display values once. A Quest color-chart
capture verified that 64, 128, 192 and 224 grey patches retain those values;
the previous sRGB source upload darkened them to approximately 13, 55, 134 and
190. The transport remains 8-bit SDR; this correction does not add HDR or higher
precision gradients.

### Experimental flat games in SteamVR

Start SteamVR first, then add `droiddeck-steamvr-game %command%` to a flat
game's Steam launch options. Remove that prefix to restore normal panel launch.
This runs a nested, unmodified Gamescope OpenVR backend for that game. Gamescope
owns the GPU overlay, placement and input routing; no CPU screen capture or
PipeWire service is used. The existing DroidDeck Vulkan synchronization layer
remains enabled. On Quest, a connected XR receiver selects Gamescope's Vulkan
texture sharing instead of DMA-BUF modifiers. Explicit environment overrides
are preserved; other devices retain the upstream modifier default.

An upstream GPU cube overlay has been observed on Quest 3. Flat-game stability,
controller interaction and performance still require hardware validation. This
uses Valve's public Gamescope backend; the exact Steam Frame OS launch setup
has not been verified.
