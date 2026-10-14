# Quest OpenXR implementation

Branch: `feat/quest-openxr`. The goal is standalone immersive SteamVR on Quest,
using DroidDeck's Linux guest and an Android OpenXR frontend.

## Milestone 1: native headset frontend

`questxr` builds a separate `com.droiddeck.questxr` companion so the frontend can
be validated without replacing the installed DroidDeck runtime. It uses the
Khronos Android loader, GLES swapchains, predicted stereo views and head poses,
Touch grip/trigger actions, and trigger-edge haptic feedback. The test scene has
world-fixed geometry and tracked controller cubes. Controller availability is
optional for the diagnostic scene; sleeping controllers remain inactive.

Build with `:questxr:assembleDebug`. `tools/deploy_quest_xr.ps1` builds, aligns,
signs with an external key directory, verifies the expected certificate and
installs the companion. Private keys must remain outside the checkout.

Run `python tools/test_quest_xr.py --adb <adb executable>`. The runner restarts
the companion, simulates wearing the headset using Meta's documented
`com.oculus.vrpowermanager.prox_close` broadcast, checks fresh advancing frame
telemetry and valid tracked head pose, and stores local JSON/logcat results in
the ignored `questxr-validation` directory. Use `--require-controllers` when
both Touch controllers are awake. That option must pass before claiming tracked
controller validation. Trigger haptics require an actual trigger press.

## Device evidence, 2026-10-14

Quest 3, Android 14, DroidDeck 0.4.0. Local Windows build with NDK 27.2.12479018
passed. The deployed companion certificate matches `keystore/release-signer.sha256`.
The automated 15-second test passed with 792 rendered stereo frames at the last
sample, a 13,888,888 ns predicted display period (72 Hz), and head location flags
15 (position/orientation valid and tracked). A focused state was observed before
an OS panel took input focus; stereo frame submission continued in VISIBLE.
Neither controller was active. Controller motion and physical haptics have not
been validated. This establishes native OpenXR rendering, not SteamVR gameplay.

Quest reported `XR_KHR_android_surface_swapchain` support. Next, validate an
Android-produced surface swapchain as a projection source, then connect the
existing Android Wayland compositor to it. This may avoid transferring Linux
dmabufs directly into the runtime's graphics device. Surface support alone does
not establish that projection, synchronization, or SteamVR rendering works.

The first launch left a controller-required OS dialog from an earlier manifest.
After declaring optional controller/hand capability and restarting VR Shell,
the app launched. Automated tests should report launch blockers rather than
treating a successful `am start` as successful rendering.

## Milestone 2: Android projection surface and guest integration

The Android Surface producer test passed repeatedly, including 1,079 submitted
stereo frames in a 15-second test at 72 Hz. The new stage-space test reports
tracked floor-relative position, and the runtime supports monotonic-time
conversion. A JNI abort during the first surface test was traced to treating
the runtime's global Surface reference as a local reference and corrected.

The same native frontend is now compiled into the main app behind the explicit
`QuestXrActivity` entrypoint. With `guest_surface=true`, it hands its Android
projection Surface to the existing compositor. Surface transitions wait for a
compositor acknowledgement after the old Vulkan swapchain's GPU work drains.
Successful guest GPU presentations are counted separately from XR submissions.
The main app's Kotlin and native targets compile locally; an on-device main-app
guest test is still required before considering this bridge functional.

The frontend publishes a versioned 296-byte seqlock tracking record under the
session runtime directory. It contains floor-relative head/controller poses,
per-eye view geometry, predicted display time converted to CLOCK_MONOTONIC,
display period, and Touch action values. The OpenVR driver consumes fresh
records for real head/controller poses and asymmetric FOV; it marks stale
tracking invalid. The modified ARM64 Linux driver links successfully in WSL.
Driver timing, eye-to-head transforms, input completeness, render-pose/image
association and return haptics still need implementation/validation.

The companion's release build now targets Android 14 and passes release lint.

## Milestone 3: input, timing and return haptics

The driver now supplies eye-to-head transforms and runtime-derived IPD, consumes
Touch trigger/squeeze/stick/button state, reports predicted pose timestamps and
paces vsync against converted OpenXR display timing. Vive-profile compatibility
maps primary buttons to trackpad click and secondary/menu buttons to application
menu. A versioned 64-byte feedback channel carries bounded driver haptic events
back to OpenXR. Repeated surface tests still submit 1,079 stereo frames at 72 Hz.
The native and Kotlin targets compile, release lint passes, and the ARM64 Linux
driver links with no undefined symbols. IPC tests cover valid packets, odd
seqlocks, version mismatch, stale/future timestamps, inactive state, excessive
dimensions and truncated files; they run in CI as well.

The first full integration CI APK compiled successfully but failed the ELF
closure check because its system-library list omitted `libGLESv3.so`. The list
now recognizes that Android system dependency. A subsequent full CI run is
required before deployment of the integrated main-app build.

On-device prerequisites: the installed app's Debugging tools toggle explicitly
rejects agent UI activation (`PROTECTED_CONTROL`); it must be switched on-device.
Steam is signed out and both Touch controllers remain inactive. These block
guest command diagnostics and actual game/controller/haptic validation, but do
not block native rendering or independent engineering. No guard was bypassed.

## Remaining stages

## Milestone 4: full build and deployment

Full CI run 38641552327 passed at `e1d09612`, including APK compilation, tracking
IPC tests and ELF closure verification. Its 62,211,156-byte APK was downloaded,
aligned and signed locally with the provided key. The installed original APK's
certificate matched the expected release certificate. Updating the standard
`com.droiddeck.launcher` package with `adb install -r` succeeded; a copy of the
previous APK is retained locally under ignored `build/installed-original.apk`.
No uninstall or data reset was performed.

The latest source adds the Quest-specific SteamVR Start flow, ongoing SteamVR
status telemetry, a clean native Back/exit path, and bounded Android event
draining. It removes UI-thread waits from native surface handoff to avoid a
NativeActivity destruction deadlock, and restores proximity after tests unless
`--keep-awake` is specified. These changes compile locally but require a fresh
integrated APK before their device validation.

Later device runs stopped rendering because the OS displayed **Finding position
in room**, saying it cannot detect headset movement. Logs show the XR session
going through STOPPING to IDLE; a sample had only flags 7, lacking tracked
position. State changes are now written immediately to health telemetry, and
tests require all four valid/tracked flags to establish 6DoF. Earlier successful
72 Hz runs remain evidence for those earlier device conditions; the latest
tests are not counted as passes. Physical headset positioning/lighting needs
attention before sustained 6DoF and game validation can resume.

The actual shared tracking file was read through the debuggable companion:
296 bytes, correct magic/version and even sequence, converted prediction about
21 ms beyond the monotonic sample time. This confirms the IPC time-domain
conversion on the headset.

## Remaining stages

- Android/Linux eye-image bridge and frame ownership/synchronization.
- OpenVR driver IPC for predicted 6DoF head, per-eye views and tracked hands.
- Real Touch input mapping and haptics returning through the bridge.
- Runtime-paced driver timing and render-pose association.
- Guest SteamVR dashboard and owned-game validation on Quest.
- Sustained timing, memory and thermal measurements; existing semaphore
  workarounds must be assessed under the real graphics chain.

References: [Meta scriptable testing](https://developers.meta.com/vr/documentation/native/android/ts-scriptable-testing/),
[Khronos Android surface swapchain](https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrCreateSwapchainAndroidSurfaceKHR.html).
