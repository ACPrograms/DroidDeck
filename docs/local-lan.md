# Local LAN experiment

Status: implemented prototype; no two-device hardware or game validation. Open
**Settings → Overview → Local LAN · Experimental** before starting a session.

## Scope

Two devices each run their own copy of a LAN-capable game. One device can create
an Android local-only hotspot; the other joins it through Android Wi-Fi settings.
No router, SIM, mobile data, internet connection, or root access is required by
the hotspot API. Android chooses the network name, password, band, and subnet.
Hardware/OEM policy can reject the request. Existing Wi-Fi can disconnect.

The separate USB path uses an IP link that Android has already configured. A
data-capable USB-C cable plus USB tethering on one device can work **only if**
the receiving device supports USB host mode and that tethering network driver
(RNDIS/NCM, depending on device). A USB-C connector alone guarantees none of this.
USB Ethernet adapters and an Ethernet cable are another possible transport.
DroidDeck does not create USB gadget functions, force USB roles, enable tethering,
or install kernel drivers.

This does not implement remote play, controller forwarding, online matchmaking,
an internet-sharing hotspot, a virtual LAN tunnel, or generic split-screen co-op.
Steam authentication, account/licensing requirements, DRM, and a game's own
network implementation still apply. Prepare and test offline mode before travel.

## Linux integration

The guest uses Android's kernel networking rather than a separate network
namespace. Selecting **Use for games** advertises an actual private/link-local
IPv4 interface, subnet, MTU, and kernel interface index through `droiddeck-net`.
The existing preload uses that snapshot for `getifaddrs()` and `if_nameindex()`;
an explicit selection overrides normal enumeration. The real index matters for
multicast membership. Broadcast addresses are computed from the real prefix.
NetworkManager's compatibility snapshot also reports the selected LAN without
claiming internet validation, an upstream gateway, or DNS. Android's default
network continues to supply the guest's resolver configuration.

Selection is process-local, survives launcher/activity recreation, and is cleared
by **Android default network** or process exit. A Wi-Fi multicast lock is held
while selected and released on reset/process death. The session rechecks the
selected interface every second, including address changes and unplug/reconnect.
An absent selected interface clears its advertised addresses rather than
substituting cellular or the usual synthetic offline address. The hotspot is
owned by a foreground service, so launching a game or leaving the setup screen
does not release the reservation. Stop it from the screen or its notification.
The service releases late reservations delivered after shutdown, and does not
restart a hotspot automatically after process death.

**Advertising a link does not install routes or bind guest sockets to an Android
Network.** `ConnectivityManager.bindProcessToNetwork()` alone is insufficient to
establish that glibc/FEX subprocess sockets obey Android's bionic networking
hooks. This prototype leaves kernel routing to Android. On the joining device,
accept “stay connected without internet” and turn off mobile data if Android
prefers cellular. First try direct IP; a visible interface does not prove that
unicast, broadcast, or multicast traffic works. VPNs, OEM firewall/client
isolation, Steam/Wine behavior, and games that insist on internet remain risks.

## Prior art and alternatives

Reviewed on 2026-10-24; no third-party source was copied.

| Approach | Source and conclusion |
| --- | --- |
| Android local-only hotspot | [Android guide](https://developer.android.com/develop/connectivity/wifi/localonlyhotspot) and [reservation lifecycle](https://developer.android.com/reference/android/net/wifi/WifiManager.LocalOnlyHotspotReservation): public API since 26, designed for nearby devices without internet. Fits ordinary Wi-Fi clients. DroidDeck currently targets API 28, so hosting requests precise location and requires Location services; a future target-33 migration must revisit `NEARBY_WIFI_DEVICES`. |
| Wi-Fi Direct | [Android guide](https://developer.android.com/develop/connectivity/wifi/wifip2p): possible without an access point, but needs discovery/group ownership/connection recovery and device capability handling. A second wireless backend is deferred until hotspot traffic is verified. |
| Steam Deck wired LAN | [dafta's USB Ethernet gadget script](https://gist.github.com/dafta/0aadeba3aa8bcbbc8b92a233977571ed), [DeckMTP](https://github.com/dafta/DeckMTP), and [GadgetDeck](https://github.com/Frederic98/GadgetDeck): useful USB gadget prior art. The script needs root/configfs, and DeckMTP documents enabling dual-role USB in firmware. A normal Android app cannot transplant that setup. GadgetDeck lists Ethernet multiplayer as a goal rather than evidence of tested DroidDeck support. |
| Steam Deck wireless LAN | [NetDeck](https://github.com/fewtarius/NetDeck) creates hotspots through Linux networking tools with root access and recommends a second Wi-Fi adapter. Its privileges and adapter assumptions differ from an Android app. |
| Android co-op streaming | [EasyControl fork](https://github.com/daitj/Easycontrol), identified from [the Thor co-op guide](https://www.reddit.com/r/AynThor/comments/1usuxsx/share_your_screen_to_another_device_for_couch/): Android-to-Android scrcpy remote control/streaming, including USB. The guide uses a Shizuku fork and runs the game on one device. A stream or ADB port forward does not create a LAN for two independent games. |

A generic cable-only fallback using Android USB host/accessory bulk endpoints
would require a new packet tunnel and an IP stack/VPN integration, with permission,
VPN coexistence, routing, MTU, UDP/multicast, and lifecycle work on both ends.
That is substantially larger than socket forwarding and is deferred pending
evidence that stock USB tethering is inadequate on the target handhelds.

## Validation before promoting the experiment

Automated coverage checks hotspot request deduplication, permission/location
failures, unsupported hardware, reservation cleanup (including late callbacks),
LAN snapshot/index/address publication, disconnect/reconnect, returning to default,
and native interface enumeration/broadcast calculation. These tests simulate
platform behavior; they do not verify a radio, cable, Android routing, or a game.

Use two physical devices, recording models, OS builds, transport, selected
interfaces/addresses, game versions, and whether mobile data/VPNs were enabled:

1. With no external Wi-Fi and mobile data off, host/join a hotspot. Check its
   credentials, leaving/reopening setup, launching Steam/Desktop, notification
   stop, permission denial, Location off, and process death.
2. In both Linux sessions compare `cat /etc/droiddeck-net`,
   `cat /etc/droiddeck-network.json`, and Python
   `socket.if_nameindex()`; confirm distinct real addresses in the same subnet.
3. Test bidirectional TCP and UDP between guest sessions, binding the real
   addresses and using a high port (for example 27842). Repeat after disconnect,
   changed subnet, Android backgrounding, and resume. Android-to-Android reachability
   alone does not establish guest-to-guest reachability.
4. Test a subnet-directed UDP broadcast and UDP multicast with membership on the
   selected interface; then test an actual game's LAN browser and direct-IP join.
   Record failures separately from successful direct-IP traffic.
5. Repeat with mobile data active and with a VPN to establish routing limitations.
6. For USB, try both host/device roles and tethering directions; confirm an
   actual interface/address before testing guest traffic. Repeat with airplane
   mode/no SIM, cable unplug/replug, and USB Ethernet adapters. Check charging
   direction, thermal load, and sustained play.

Keep this experimental until those results exist; successful builds are not
evidence of working multiplayer.
