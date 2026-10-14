# HelixSR integration plan

Status: research only, nothing tested on the Thor yet. Reviewed 2026-10-13 against HelixSR 1.9.0
(pre-release, 2026-10-13) and 1.8.1 (latest stable).

## What HelixSR is

[HelixSR](https://github.com/lonewolf0622/HelixSR) runs NVIDIA's DLSS "Model E" super-resolution
network as plain D3D12 compute shaders (cs_6_6, 16-bit types, wave intrinsics), so it needs no
Tensor Cores and no NVIDIA driver. It is built for AMD RDNA on Proton and Windows. It works like
DLSS: it uses DLSS jitter and render sizes and offers every mode from DLAA to Ultra Performance.
It does super resolution only, with no frame generation of its own.

- **Closed source.** It ships under the HelixSR Freeware License: you may run it and redistribute
  it unmodified and complete, but you may not modify, decompile or disassemble it. The setup
  scripts are readable but fall under the same license (scripts up to 1.8.1 are Apache 2.0).
- **No NVIDIA material in the download.** The setup downloads NVIDIA's `nvngx_dlss.dll` 310.7.0
  from `github.com/NVIDIA/DLSS`, after asking and with a pinned SHA-256. It then pulls the PTX
  kernels and weights out of it, translates PTX to HLSL (`ptx2hlsl.py`) and compiles them with
  DXC. The result is embedded in HelixSR's DLL. **The built DLL contains NVIDIA's network and
  must never be redistributed**, so DroidDeck cannot ship a ready-made one. It has to be built on
  the device.
- **Three ways into a game** (`setup/helixsr_install.py`):
  - **f**: replaces the game's FSR 3.1 DLL (`amd_fidelityfx_upscaler_dx12.dll` /
    `amd_fidelityfx_dx12.dll`) and keeps the original as `*.original.dll`. Select **AMD FSR** in
    the game.
  - **o**: installs the official OptiScaler 0.9.4 (as `dxgi.dll` and similar) with its FSR 3.1
    backend pointed at HelixSR. Select **DLSS**, XeSS or FSR 2 in the game.
  - **d** (experimental): installs a `dxgi.dll` loader plus HelixSR as `nvngx.dll`. The loader
    spoofs an NVIDIA GPU and answers the DLSS signature check.

  Everything replaced goes to `HelixSR/backup`, and uninstall puts it back.
- **Runtime knobs:** `helixsr.ini` next to the DLL (`[Upscaling] Int8`, `NetworkResolution`,
  `UpscaleFilter`; `[Sharpening]`; `[Compatibility] WaveSize`), and the environment variable
  `HELIXSR_INT8`. It writes `helixsr.log` next to the DLL.
- **Cost on the developer's BC-250** (RDNA1 at 2 GHz): 1.13 ms for 720p→1080p Quality and
  0.75 ms for 360p→1080p Ultra Performance.

## The Adreno state of the art (the video)

The video, ["DLSS on Adreno | HelixSR Tested on Odin 3"](https://youtu.be/ZW670_3U7bE) (blackheart909,
2026-10-12), shows an **unofficial HelixSR 1.8.0 adaptation** running on an Odin 3 (Adreno 830)
under Armada Linux, through the FSR 3 slot. PRAGMATA works, with a "performance hit" its author
says still needs optimising, and slow startup and loading. Witcher 3 shows black flickering on
fast camera moves that is still unsolved. The description says the Thor/Android port "has not been
verified running".

How their port works ([HelixSR-Snapdragon-V762](https://github.com/dawoodtogoo2020-code/HelixSR-Snapdragon-V762),
[HelixSR-Decky-Armada](https://github.com/dawoodtogoo2020-code/HelixSR-Decky-Armada)):

1. They run HelixSR 1.8.0 in a probe under Proton ARM64 with vkd3d-proton (`44cf7c2`), and dump the
   SPIR-V for four kernels with `VKD3D_SHADER_DUMP_PATH`.
2. They rewrite that SPIR-V so that wave32 logic runs on a physical 64-lane subgroup. Bindless CBV
   accesses are split into subgroup-uniform loads, because two logical waves now share one
   subgroup and the CBV index is no longer uniform across it.
3. They install the rewritten shaders as runtime overrides. The pipeline is pinned by hash to
   1.8.0. Users must provide the "private" dumps themselves, and the README says the extraction
   is not clean yet.

The HelixSR author allowed this in [issue #3](https://github.com/lonewolf0622/HelixSR/issues/3).

**Why we probably do not need any of that:** Adreno has no wave32. Turnip's subgroup sizes are 64
and 128, so 1.8.x's `[WaveSize(32)]` kernels cannot run as written. **1.9.0 fixes this upstream
for Vega:**

- The setup now builds every kernel twice, once as `[WaveSize(32)]` and once as `[WaveSize(64)]`
  (the `_w64` variants, with cross-lane reads through LDS on `k5*`, `k7`, `k9`–`k11`).
- At run time the DLL probes the device's lane range. Its log strings include "device lanes
  %u..%u" and "the GPU has no wave32 ... every kernel at 64 lanes".
- `[Compatibility] WaveSize = 64` forces the 64-lane path.

On vkd3d-proton over Turnip, `WaveLaneCountMin` should come back as 64, so **unmodified 1.9.0
should choose wave64 for every kernel by itself**. That would make the SPIR-V patching and its
CBV-split workaround unnecessary. It should also remove the most likely cause of the Witcher 3
flicker, which is 32-on-64 emulation. This is the first thing to verify on the device (Phase 0).

## Fit with DroidDeck

| HelixSR needs | DroidDeck today |
| --- | --- |
| D3D12 + SM 6.6, 16-bit types, `WaveSize(64)` → `VK_EXT_subgroup_size_control` | vkd3d-proton per Proton build, `VKD3D_SHADER_MODEL=6_9` / FL `12_2` by default; Turnip a7xx (subgroup 64..128, fp16) |
| Python 3 + numpy to build the network | Debian arm64 rootfs already has `python3` (`droiddeck-game-env` is Python); numpy is one apt package |
| `launch_synth` (prebuilt only for x86-64 Linux and Windows) | Build it from the shipped `model.cpp` with arm64 `g++` (the setup does this itself when no prebuilt runs), or run the x86 one under FEX |
| DXC (the setup runs the Windows `dxc.exe` through Proton's Wine) | Either Windows DXC through our ARM64 Proton plus FEX (slow but no new code), or an aarch64 Linux `dxc` built in our CI from Microsoft's MIT/NCSA source (fast, preferred) |
| `nvngx_dlss.dll` 310.7.0 (or 310.2) | Download from NVIDIA after user consent, or reuse a copy already in an installed game (`--dlss <path>`), like `Lossless.kt` reuses Steam's `Lossless.dll` |
| Per-game DLL placement + `WINEDLLOVERRIDES` for `dxgi`-named OptiScaler / loader | Per-game profiles, launch wrapper `droiddeck-game-env` that already stages component files before Proton starts |
| Somewhere to turn it on | Components page (packages) + per-game Game environment profile |

`setup/helixsr-setup.sh` refuses to run on anything but x86-64 (`uname -m`), and we may not modify
it. So **DroidDeck drives `setup/helixsr_setup.py` directly**, the same way the `.sh` does. That
counts as running the scripts, not modifying them:

```
python3 setup/helixsr_setup.py <OUT_DIR> --yes --dxc <dxc> [--dlss <nvngx_dlss.dll>]
```

## Plan

### Phase 0: feasibility on the Thor (manual, needs the device)

Goal: find out whether stock 1.9.0 runs on Adreno 740, what it costs, and whether it looks right,
before any app code is written.

1. In the rootfs:
   - `apt install python3-numpy g++`.
   - Get DXC: start with Windows `dxc.exe` through ARM64 Proton, and time it against an aarch64
     `dxc` built on the Mac with Docker.
   - Run `helixsr_setup.py` with `--dlss` pointing at a 310.7.0 DLL. Record wall time, peak RAM
     and thermals.
2. Install by hand into one FSR 3.1 game using the **f** way. PRAGMATA is a good first pick
   because the video has a reference.
3. Read `helixsr.log`:
   - The `wave size:` line should say `device lanes 64..128 ... network kernels wave64`.
   - Note the `int8 network:` line. Qualcomm is probably "no fast int8". Try `HELIXSR_INT8=1`,
     since a7xx Turnip exposes `VK_KHR_shader_integer_dot_product`.
4. Measure the upscale pass with the Perf HUD at 1080p output in Quality, Performance and Ultra
   Performance, and compare against the game's own FSR 3.1. Back-of-envelope: Adreno 740 has about
   a third of the BC-250's fp16 throughput and about a seventh of its memory bandwidth, so expect
   **roughly 4–10 ms** at 720p→1080p. Ultra Performance or Performance mode may be the only
   practical ones on the Thor.
5. Repeat with Witcher 3 (the DLSS game in the video) using the **o** way, to test OptiScaler under
   our Proton/FEX and to check the flicker.
6. If the kernels fail, gather `VKD3D_SHADER_DUMP_PATH` dumps and the vkd3d log. Failures are
   driver bugs we can fix in our own Turnip or vkd3d builds, not HelixSR bugs we may patch.

Go/no-go: wave64 runs, the image is correct, and the cost at some mode is below what a native
resolution step would save.

### Phase 1: HelixSR as a component (build the network on device)

- **Package entry.** Add `HelixSR` to the Components catalog. Download the official release zip
  from GitHub releases, pinned to version and SHA-256, and never mirror it (the license allows
  only unmodified, complete redistribution; fetching from upstream avoids any question). Show the
  HelixSR license, plus NVIDIA's DLSS license notice before the DLSS download.
- **DXC.** Ship it as a DroidDeck-built package (aarch64 Linux `dxc`, pinned to the same DXC
  release HelixSR pins: v1.9.2607), with our own license notice.
- **Build job.**
  - Runs on the existing background queue (`AutoComponents`-style: low priority, `nice`/`ionice`,
    waits while a session runs, and waits on battery saver or thermal limits).
  - Steps: unpack the zip, `apt`-free check for numpy (or bundle numpy in the rootfs image), find
    the DLSS DLL, run `helixsr_setup.py`, and parse its `[helixsr-setup]` progress lines into the
    UI progress bar.
  - Output goes to a DroidDeck-owned folder in the rootfs, for example
    `/root/.local/share/droiddeck/helixsr/<version>/`.
- **DLSS source.** First look for `nvngx_dlss.dll` 310.7.0 or 310.2 in installed Steam, GOG or
  Epic games, the same way `Lossless.kt` finds Lossless.dll. Otherwise offer the NVIDIA download
  after explicit consent. Delete the DLL afterwards, as the setup does.
- **Rebuilds.** Rebuild when the HelixSR version changes, and keep the previous build until the new
  one succeeds.

### Phase 2: per-game upscaler injection

- Add **Upscaler** to the per-game profile: *Off* / *HelixSR (FSR 3.1 slot)* / *HelixSR via
  OptiScaler (DLSS/XeSS/FSR2)*. Show only the ways that work for that game's files (scan the game
  folder for the FSR 3.1 / `nvngx_dlss.dll` / XeSS DLLs, as `droiddeck-game-env`'s `game_files`
  does).
- **Applying it.** Start with option (a) and keep (b) as a fallback.
  - **(a)** Call HelixSR's own installer with `HOME` pointed at an empty directory, so its Steam
    scan finds nothing, plus `--folder <game>`. It keeps HelixSR's backups, markers and uninstall
    behaviour exactly.
  - **(b)** Use DroidDeck's own file sync. It does the documented manual-install steps with our
    own managed-file manifest, the same pattern as the per-game DXVK/VKD3D overrides.
  - **When:** at profile change, never mid-session. Re-check at launch in `droiddeck-game-env`,
    because a Steam update or "verify files" puts the game's DLLs back. When that happens,
    re-apply quietly or show a notice.
- **Environment.**
  - The **o** way: add `WINEDLLOVERRIDES=dxgi=n,b` (or whatever DLL name OptiScaler took) to the
    game's environment.
  - Expose `HELIXSR_INT8` and generate a `helixsr.ini` (Int8, WaveSize, sharpening,
    NetworkResolution) from profile settings rather than asking users to edit an ini.
- **Turning it off** runs the uninstall and restores the originals. Reset game profile must do the
  same.
- **Leave out the experimental d way** (DLSS loader and NVIDIA spoof): it is unsafe with
  anti-cheat, tested only on desktop Linux, and depends on nvapi on ARM Proton. Revisit later.

### Phase 3: in-session UX and diagnostics

- Show the active upscaler and the `helixsr.log` wave/int8 lines in the Perf HUD or session
  overlay, and attach `helixsr.log` and `OptiScaler.log` to session log shares (`SessionLogShare`).
- Explain the Insert-key OptiScaler menu for pad users. Map it to a pad combo, or document the
  QAM shortcut.
- Add `docs/development/helixsr.md` and an entry in Components' license notices (OptiScaler
  GPL-3.0, fakenvapi MIT, AMD FidelityFX license; all of these come from the official zip).

## Risks and open questions

- **Performance on Adreno 740** is the deciding factor, and the Odin 3 (830) already reports a
  visible hit. Without fp16 dot or int8 acceleration, HelixSR may only be worthwhile in Ultra
  Performance or with `NetworkResolution` reduced.
- **Licensing.** HelixSR's license forbids modification but allows running and unmodified
  redistribution. We only download, run and copy its files as its own installer does, which is
  fine. The built DLL contains NVIDIA's network, so it must never leave the device: no cloud
  backup of game folders with it, and keep it out of log shares and save-sync. Ask the author
  (issue or Discord) whether a DroidDeck integration that drives `helixsr_setup.py` directly is
  welcome. It costs nothing and they have been receptive (issue #3).
- **Project velocity.** Seven releases in a week, and 1.9.0 is a pre-release with an open int8
  flicker bug (#44). Pin versions and let users choose stable or latest.
- **Build cost on device.** The PC build takes about a minute. On the Thor, with DXC under
  Proton/FEX, it could take tens of minutes, which is why the native aarch64 `dxc` matters.
- **OptiScaler under ARM64 Proton + FEX** has not been tested in DroidDeck. That needs testing
  for the **o** way to be useful.
- **D3D12 only.** DX11 games need OptiScaler's DX11-on-12 path, which is out of scope.
