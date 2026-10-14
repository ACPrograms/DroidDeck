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
