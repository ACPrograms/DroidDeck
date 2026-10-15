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

## Milestone 4: full build and deployment

Full CI run 38641552327 passed at `e1d09612`, including APK compilation, tracking
IPC tests and ELF closure verification. Its 62,211,156-byte APK was downloaded,
aligned and signed locally with the provided key. The installed original APK's
certificate matched the expected release certificate. Updating the standard
`com.droiddeck.launcher` package with `adb install -r` succeeded; a copy of the
previous APK is retained locally under ignored `build/installed-original.apk`.
No uninstall or data reset was performed.

At that stage, source added the Quest-specific SteamVR Start flow, ongoing SteamVR
status telemetry, a clean native Back/exit path, and bounded Android event
draining. It removes UI-thread waits from native surface handoff to avoid a
NativeActivity destruction deadlock, and restores proximity after tests unless
`--keep-awake` is specified. These changes compiled locally and subsequently
shipped in the integrated APK described in the final build evidence.

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

## Milestone 5: owned image and original render-pose protocol

The opt-in `DROIDDECK_VR_PRESENT=questframes` presenter captures an owned RGBA
side-by-side image and its original SteamVR render geometry. The driver retains
each submitted `mHmdPose` and `mProjection` and composes the same eye-to-head rig
configured at HMD activation. It does not substitute the newest tracked pose
for an older image. The versioned 264-byte header and pixels travel together
over a private, same-UID Unix socket. Acknowledgement means the receiver owns a
complete CPU copy, not that an XR layer was displayed.

The capture path holds `Present` until its Vulkan copy fence completes. It
refuses missing, empty, errored or timed-out producer DMA fences, unsupported
multiple layers and malformed render geometry. It never substitutes the old
4 ms delay for synchronization. **Kernel producer-fence registration on the
Quest graphics chain is not yet established**, and KGSL may reject the export
entirely. Consequently this is an experimental correctness-first path, not a
verified synchronization solution. The existing compatibility Vulkan layer
still drops some imported semaphore waits; that also needs resolution before
gameplay correctness can be claimed.

With `frame_input=true`, the native frontend uses explicitly acquired GLES XR
swapchain images, uploads receiver-owned pixels, and submits the captured
per-eye poses/FOV. It omits stale images. `frame_verify=true` reads uploaded
pixels back from the GPU and compares every byte. This mode is separate from
the asynchronous Android Surface diagnostic. The main activity's explicit
`guest_frames=true` entrypoint detaches the desktop Surface, keeps the guest
active and starts the same SteamVR helper when requested. The Linux helper
selects the experimental presenter only when this private receiver exists.
`tools/test_quest_xr.py --guest --steamvr` uses that entrypoint and requires owned
source images/frame IDs to advance, not generic desktop presentations. It is
not yet the normal Quest Start flow and does not provide a zero-copy or
performance-ready bridge.

Host tests cover image/pose pairing, rotation and asymmetric projection math,
latest-frame replacement, copying before acknowledgement, private socket
ownership, restart/stale-socket recovery, replay rejection, malformed/truncated
input and deadlines. Address/undefined-behavior sanitizer tests pass. A separate
test confirms eight concurrent blocking presenters exit when Vulkan cannot
initialize. Android test executables compile with NDK 27.2.12479018; the main
native target and companion APK also compile. These are host/build results,
not headset GPU evidence. The Quest subsequently disappeared from ADB, so the
new path cannot yet be deployed or validated on-device.

`tools/build_quest_xr_tests.ps1` builds the three Android test executables.
`tools/test_quest_frame_channel.py --adb <adb> --native-tests <directory>` runs
the Android protocol test and a known-pattern sender under the companion's own
UID. It checks the final source frame ID and GPU checksum. Add
`--require-rendering` to additionally require sustained tracked projection.
Without that option it proves only private image transport/GLES upload, not
6DoF, SteamVR or games. It never enables the main app's protected Debugging tools.

The failed full build at `7277386c` was a Debian archive HTTP package 404. CI now
bootstraps CA certificates, uses HTTPS for compiler packages and retries
downloads while retaining APT signature/package-hash verification.

The signed companion for this stage is retained locally under ignored
`build/questxr-owned-frames-signed.apk`. Deployment supports `-SignOnly` while
disconnected; before installing it now requires an authorized, explicitly
selected Quest 3, rather than risking an unrelated Android device.

### Reconnected device evidence

Full CI run 38649944478 passed at `040d31c1` and its APK was downloaded and
release-signed locally. After USB reconnection, the updated diagnostic companion
installed successfully. The private image test passed on the actual Quest:
144 complete frames received, zero rejections, 137 uploaded, final source and
GPU-verified frame ID 144, and expected GPU checksum 1815165381. Every byte of
the owned image matched GLES readback. The protocol unit executable also passed
on Android. These prove image ownership/transport and real GPU upload, not
Linux producer synchronization or SteamVR gameplay.

A later repeat passed both tracking and frame protocol unit executables on
Android but lost the sustained session at image 138. The native tracking test
also failed: 9 rendered frames, session stopped to IDLE, head flags 7 and no
active controllers. State reports still show Debugging tools off and no running
guest. Steam sign-in is pending. Test failures are retained, not reclassified
as passes. The user cannot attend these prerequisites yet and requested a pause
after the remaining independent work.

The driver now marks position/orientation that are merely valid but untracked
as out-of-range and rejects nonfinite or non-unit poses. The importer requests
Vulkan 1.2 for its core image-format-list use. Focused Kotlin tests and the new
validation classifier tests pass. The broad Linux suite had one unrelated
AppImage log-tail failure under WSL; the full CI suite passed at `040d31c1`.

### Final build and requested pause

Full CI run 38652176155 passed at `2ef2d4b5`, including the expanded IPC tests,
Kotlin/Python tests and ELF checks. The APK was downloaded, release-signed with
the external key and installed over the standard package with `adb install -r`.
The device provider confirms `remote clean · feat/quest-openxr at 2ef2d4b`,
version 0.4.0, retained r9 runtime and an idle session. MainActivity launches
without an AndroidRuntime error. No uninstall or data reset was used.

A final cosmetic shader-variable cleanup compiled for both native targets and
was deployed in the companion. Its repeated device test passed both Android IPC
executables and the known-pattern GPU upload: 144 received, zero rejected,
126 uploaded, source/GPU-verified ID 144 and checksum 1815165381. The earlier
interrupted repeat remains a recorded failure; sustained tracked rendering and
guest SteamVR remain unverified. The unrelated AppImage test suite passed all
eight cases on a focused rerun without code changes.

The diagnostic companion is stopped, simulated proximity is restored to far,
and the USB stay-awake setting introduced during testing is restored to off.
The user requested pausing the goal until they can help with Steam sign-in,
on-device Debugging tools, tracking and controllers. The goal is not complete.

### Resumed device work

Steam sign-in and the on-device Debugging tools prerequisite are now satisfied.
The user's left Touch controller tracks in space and gives a short trigger
pulse in the native scene. The right controller also fails in Quest Home, so
two-controller validation remains blocked by a controller/Quest-side issue.
The native scene reports tracked head flags 15 and a roughly 8.333 ms display
period (120 Hz). This does not establish guest SteamVR image correctness.

The selected imported Android/Linux drivers failed frame import. Selecting the
bundled Quest-patched Android driver and the Linux runtime default, disabling
Cardboard for the ordinary 2D panel, and restarting the app process restored
Steam's first frame and READY. These were combined configuration changes, not
an isolated single-driver comparison. Original preferences are backed up
locally. No uninstall or data reset was used.

Valve's pinned ARM64 SteamVR package installed successfully. The guest driver
receives real per-eye texture handles and imports them on the Quest GPU, but
the correctness-first capture path rejects the unavailable producer fences.
The first owned-image test submitted zero projection images; it remains a
failure. The ordinary Wayland diagnostic can present SteamVR frames but does
not establish producer synchronization or image/pose association.

The Vsync scheduler now rejects repeated predicted targets and PostPresent
uses the same atomically paired runtime tick/period instead of a fixed 60 Hz
interval. Host and actual Android CPU regression tests pass for 72/90/120 Hz,
throttling, prediction reuse, concurrent tick/period publication and restart.
The updated ARM64 driver links and was copied into the live runtime with its
SHA-256 verified. This does not yet establish sustained guest rendering.

The 2D panel's onStop could also suspend the guest after the XR surface became
visible. XR visibility now has a separate service lease; panel transitions
cannot overwrite it, and normal suspend rules return when XR visibility is
lost. Focused visibility/background-policy tests pass. A signed full-app build
and live validation of this lifecycle fix are pending. The lease is acquired
before native initialization so removing the old panel task cannot end the
guest during the handoff, and it is explicitly released on stop/destruction.

SteamVR's virtual display still advertised 1920x1080 at 60 Hz while the XR
driver advertised 3360x1760 at 120 Hz. The compositor could not find a matching
surface and crashed after its first submission. A controlled runtime override
to the XR geometry restores the headless display surface and sustained 120 Hz
submission: over 10,000 Present calls were observed. Owned images remain at
zero because the producer-fence check correctly refuses them.

The helper now obtains display geometry from fresh, validated tracking using a
small ARM64 reader built against the same IPC headers. Explicit user overrides
remain unchanged and older/stale sessions retain the normal fallback. Host
tracking tests and four shell-helper regressions pass. The actual Quest reader
prints 3360x1760@120, and automatic selection reproduces sustained submission.
Startup stdout is now retained in Steam's guest log directory, with the old
runtime-log path preserved as a symlink; a session restart no longer loses it.

A minimal native Vulkan probe on the real Quest reports export/import support
for SYNC_FD fences/semaphores, but not OPAQUE_FD handles. This distinguishes the
unsupported implicit DMA-buf fence export from available explicit Vulkan GPU
completion primitives. Implementing the explicit producer synchronization is
still necessary. The desktop's dashboard helper also
lacks X display authorization; no authentication or permission check was
bypassed to work around that error.

The Linux Vulkan GPU regression in `tools/vr/tests/test_vulkan_sync_fd.cpp`
submits an actual GPU fill with an exportable SYNC_FD fence, checks its kernel
sync-file payload, waits for the signal, and verifies every mapped value.
The Quest run exported descriptor 5, reported one initially pending kernel
fence, signaled successfully and matched all 1,024 values. This proves an
explicit Vulkan GPU completion primitive on this graphics stack, not the
as-yet-unimplemented cross-process producer lease or SteamVR game correctness.
`tools/vr/build.sh` builds its ARM64 executable under `build/vr`; it is not
bundled into the user-facing runtime. A valid already-signaled SYNC_FD value of
-1 is handled according to the Vulkan specification.

## Remaining stages

### Sustained validation telemetry

`tools/test_quest_xr.py --metrics` records public battery, thermal-service and
package-memory dumps before and after the measured frame window. Unavailable
metrics remain explicitly unavailable; no privilege escalation or alternate
private-file access is attempted. Reports now distinguish observed XR submits,
guest GPU presentations, owned-image uploads and rejected-frame rates from the
runtime's nominal refresh rate, and record the focused/tracked sample fraction.
A run that loses focused 6DoF tracking at the end no longer passes merely because
one earlier sample was tracked. These reporting paths have host tests; no new
Quest performance or thermal result is claimed while the headset is disconnected.

### Touch input profile

Fresh XR controller activation now selects SteamVR's built-in `oculus_touch`
profile and handed Quest Plus render model instead of presenting real Touch
controllers as Vive wands. Face buttons, joystick click, left menu, analog grip
and capacitive touches are separate inputs. A/B and X/Y no longer double as
trackpad or menu clicks; the Quest's reserved system button is not fabricated
from its left application menu. Native OpenXR captures trigger, thumbstick,
thumbrest and face-button touch actions in previously unused button bits, keeping
the 296-byte tracking layout and existing low click bits compatible. Inactive
hands clear every input; invalid analog values are sanitized. The flat Cardboard
gamepad path retains its existing Vive profile and mappings.
Hand poses now carry the converted OpenXR prediction timestamp relative to
SteamVR's clock, matching the headset instead of reporting a future hand pose
as a present-time sample. The common timestamp conversion is host-tested for
future, past and unavailable prediction times.

Host tests cover the input decoder's independent clicks, touch-versus-press,
handed menu, analog ranges and inactive reset. The Android native frontend and
Linux driver compile locally. SteamVR profile acceptance, game bindings and the
physical right controller still require connected-device validation. Profile
and component paths follow the [OpenXR Touch profile](https://registry.khronos.org/OpenXR/specs/1.0-khr/html/xrspec.html#_oculus_touch_controller_profile)
and ALVR's [OpenVR properties](https://github.com/alvr-org/ALVR/blob/ebec107b7e5f81677fb9c631d239daf2110bf573/alvr/server_openvr/src/props.rs)
and [input paths](https://github.com/alvr-org/ALVR/blob/ebec107b7e5f81677fb9c631d239daf2110bf573/alvr/server_openvr/cpp/alvr_server/Paths.cpp).

### Experimental producer lease

`DROIDDECK_VR_PRODUCER_LEASE=1` opts the compositor into a private, same-UID,
PID-authenticated producer endpoint. It serializes producer queue submissions,
inserts real GPU fence markers on every observed producer queue, and grants a
lease only after those fences complete. Producer submissions remain frozen
until the presenter's GPU readback finishes and the release handshake succeeds.
The presenter device is excluded from the producer lock. Multiple producer
PIDs in one eye pair are rejected. Pending timeout fences are retained, not
destroyed while the GPU can still use them. Losing a granted lease quarantines
submissions instead of resuming writes against a potentially incomplete copy;
restarting SteamVR is required in that case. The layer remains resident for its
process lifetime so its service threads cannot execute unloaded code.

The protocol regression verifies private sockets, peer PID, completion gating,
retention beyond client acquisition deadlines, release acknowledgement and
disconnect quarantine. A real Vulkan Lavapipe regression waits for deliberately
delayed GPU work, verifies all 1,024 buffer words, blocks a concurrent overwriter
through the lease, then verifies its writes after release; it also exercises
an outstanding fence through repeated timeouts. CI installs the software Vulkan
driver so this is not merely a source-shaped or mocked GPU test.

This route is intentionally opt-in, not declared complete. Live Quest validation
must establish that the swap-set PID identifies the actual final-image writer,
audit the compositor's external-memory image-layout/queue-family release
contract, and validate the matching acquire/release barriers with real images.
The host buffer test does not establish any of those image-sharing properties.
Owned-image submission still fails closed without a successfully retained lease.

The Vulkan layer no longer drops imported binary waits or unsignaled timeline
waits after a three-millisecond grace period. Queue submissions and host waits
retain their original arguments, deadlines and downstream results. The
software fallback supports permanent OPAQUE_FD timeline payloads only; binary,
temporary and malformed fallback imports fail explicitly without consuming
the caller's descriptor. Native handles still pass through unchanged. Host
regressions exercise both submit APIs, pending values, device-loss propagation,
65-semaphore host waits, import failures, re-export and descriptor cleanup.
These are API-contract tests, not Quest GPU or SteamVR game validation. Native
binary SYNC_FD support remains available, but adapting SteamVR's unsupported
OPAQUE_FD binary usage still requires a correctly synchronized implementation.

Steam's ARM64 watchdog also aborts with `futex robust_list not initialized by
pthreads`. A captured preload trace showed successful own-thread queries
leaving `errno=EFAULT`. The preload now prefers the real kernel query, falls
back only for `ENOSYS`, preserves errno on fallback success and safely rejects
invalid output pointers. A host regression compares native and fallback
results with the kernel's actual head, including a held robust mutex and a
second thread, and preserves permission failures. This is not yet a verified
fix for Valve's assertion: the Quest disconnected before a new device trace.

- Android/Linux eye-image bridge and frame ownership/synchronization.
- OpenVR driver IPC for predicted 6DoF head, per-eye views and tracked hands.
- Real Touch input mapping and haptics returning through the bridge.
- Runtime-paced driver timing and render-pose association.
- Guest SteamVR dashboard and owned-game validation on Quest.
- Sustained timing, memory and thermal measurements; existing semaphore
  workarounds must be assessed under the real graphics chain.

References: [Meta scriptable testing](https://developers.meta.com/vr/documentation/native/android/ts-scriptable-testing/),
[Khronos Android surface swapchain](https://registry.khronos.org/OpenXR/specs/1.1/man/html/xrCreateSwapchainAndroidSurfaceKHR.html).
