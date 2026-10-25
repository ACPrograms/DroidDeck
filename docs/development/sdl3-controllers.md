# SDL3 controller bridge

The integrated bridge runs in DroidDeck's `:sdl3` process. Open **Settings → SDL3Bridge**
or the SDL3Bridge page in the session drawer to configure up to four controllers.
USB controllers use SDL3's HID drivers; Android input remains the fallback for
Bluetooth and built-in controllers. The embedded driver only claims USB controllers:
Android events are delivered to DroidDeck's activity and cannot reach the separate
headless SDL process. Steam Native Hook-up forwards a USB controller's
identity and reports to Steam so Steam can use its own controller driver.

USB access requires Android's device permission. The optional floating control also
requires permission to display over other apps. A separate SDL3Bridge installation
should be stopped before using the integrated bridge with the same USB controller.
Gyro, touchpad, remapping and lightbar settings belong to Steam while Hook-up is active.

## Source and attribution

The controller implementation is by [pablo89898991](https://github.com/pablo89898991),
imported from [SdlBridge at 63feeac6](https://github.com/pablo89898991/SdlBridge/tree/63feeac6)
through [SDLApiDroidDeck at 131f1cef](https://github.com/pablo89898991/SDLApiDroidDeck/tree/131f1cef).
The imported commit retains Pablo's Git author identity. DroidDeck integration and
build adjustments are recorded separately.

The port keeps DroidDeck's name, signing, workflows and test failure handling. It
excludes the fork's ADPF changes, cursor-compositing default, Steam controller-rate
override, and CI/download fallbacks.

SDL 3.4.18 is downloaded by CMake from its official release archive and verified
against its SHA-256 digest. Its Android Java classes are imported from the same
release. `HIDDeviceManager.java` retains the bridge's marked Bluetooth-permission
adjustment. SDL uses the zlib license, included in
`app/src/main/assets/licenses/sdl3/LICENSE.txt`; the bridge is distributed under
DroidDeck's GPLv3 license.

## Hardware checks

Check that the built-in controller still navigates DroidDeck and Steam with the
SDL3 driver both enabled and disabled. Open the bridge settings and controller test
screen, then start and stop a session without a USB controller attached.

With a USB controller, grant access and verify hot-plug, buttons, sticks, gyro,
touchpad and rumble in presented-controller mode. Repeat with Steam Native Hook-up,
including switching back, disconnecting, and reconnecting. Verify multiple player
slots and Steam on the desktop when the corresponding hardware is available.
